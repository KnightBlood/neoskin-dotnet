using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Paths;
using NeoSkin.Web.PluginHost;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Cookie;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service.Crypto;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;

namespace NeoSkin.Web;

/// <summary>
/// 应用的装配入口（Program 与测试共用）。
///
/// <p>当前装配：路由表 + 分发器 + cookie/会话/CSRF/语言中间件。
/// 控制器 handler 尚未接线（未实现的路由回 501）。</p>
/// </summary>
public static class NeoSkinApp
{
    public static WebApplication Create(string[] args, IHandlerResolver? resolver = null,
        NeoSkinSettings? settings = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var app = builder.Build();
        // 让资源清单按实际 wwwroot/app 解析哈希（CI 使用 6.0.10 发布包时也能命中）。
        Support.AssetManifest.WebRoot = app.Environment.WebRootPath;

        settings ??= NeoSkinSettings.FromEnvironment();
        Routes routes = Routes.Load();

        byte[]? key = settings.KeyBytes();
        EncryptCookies? cookies = key is null
            ? null
            : new EncryptCookies(new PhpEncrypter(key, "aes-256-cbc"));
        ISessionHandler session = new FileSessionHandler(
            Path.Combine(settings.Storage.Root.Length == 0 ? Directory.GetCurrentDirectory() : settings.Storage.Root, "framework", "sessions"), 120);

        var database = new Database(settings);
        var schema = new SchemaInstaller(database.Open, settings);
        var paths = new AppPaths(Directory.GetCurrentDirectory(), settings);
        var option = new Option(database, schema, paths);
        try
        {
            if (schema.IsInstalled())
            {
                new Db.SchemaMigrator(database, schema, option, settings).Migrate();
            }
        }
        catch (Exception e) when (e is NotSupportedException
                                      or System.Data.Common.DbException
                                      or InvalidOperationException)
        {
            // 数据库不可用或驱动未接入时跳过迁移，不影响站点启动
        }
        finally
        {
            if (settings.Database.Connection == "sqlite")
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        var users = new UserRepository(database);
        var players = new PlayerRepository(database);
        var textures = new TextureRepository(database);
        var closet = new ClosetRepository(database);
        var reports = new ReportRepository(database, users, textures);
        var languageLines = new LanguageLineRepository(database);
        var communications = new CommunicationRepository(database);
        var notifications = new NotificationRepository(database);
        var communicationsService = new Service.Communications(communications, notifications,
            users, settings);
        var loader = new TranslationFileLoader();
        var cipher = CipherConfig.Create(settings);
        var failures = new LoginFailCounter();
        var mailer = new Mailer(settings);
        string storageRoot = settings.Storage.Root.Length == 0
            ? Directory.GetCurrentDirectory()
            : settings.Storage.Root;
        var oauth = new Service.OAuth.OAuthService(storageRoot,
            new Service.OAuth.OAuthStore(database, settings.Timezone), users, cipher, key,
            settings.Timezone);
        var handlerResolver = resolver
            ?? new NeoSkinHandlerResolver(users, players, textures, closet, reports, languageLines,
                communications, notifications, database, cipher, option, settings, oauth, mailer,
                failures, paths.Textures, paths);

        // `RedirectToSetup` 在已安装且 options.version 落后时执行 `php artisan update`。
        var updates = new Service.UpdateRunner(database, schema, option, settings, paths,
            loader.ClearCache);

        MiddlewarePipeline pipeline = MiddlewarePipeline.CreateDefault(
            cookies, session, SessionCookieConfig.Default,
            key is null ? null : new PhpEncrypter(key, "aes-256-cbc"),
            Locales.AppLocale, users, option, loader, oauth, paths, settings, updates);

        var dispatcher = new RouteDispatcher(routes, handlerResolver,
            pipeline, option, settings);

        var pluginRuntime = new PluginRuntime(new PluginManager(option, settings, paths));
        pluginRuntime.Start();
        PluginRuntime.Current = pluginRuntime;
        app.Lifetime.ApplicationStopping.Register(pluginRuntime.Dispose);

        // 前端静态资源（wwwroot/，部署时把源项目 public/app 拷进来）
        app.UseStaticFiles();

        // /lang/<locale>.js：由前端语言包生成（源里由 JavaScript 服务写 public/lang）
        var bundle = new JavaScriptBundle(loader,
            new PluginManager(option, settings, paths));
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/lang/", StringComparison.Ordinal)
                && path.EndsWith(".js", StringComparison.Ordinal))
            {
                string locale = path[6..^3];
                context.Response.ContentType = "application/javascript; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-cache, private";
                await context.Response.WriteAsync(bundle.Generate(locale));
                return;
            }

            await next();
        });

        // 内部探活（非 Laravel 路由，供部署检查用）。必须在路由分发器（终端中间件）
        // 之前处理：WebApplication 的 MapGet 端点会在终端 Run 之后才执行。
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/__neoskin/health")
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("ok");
                return;
            }

            await next();
        });

        // SPA 壳与 /api/bootstrap：不经过 174 条 PHP 路由表，单独挂载。
        app.Use(async (context, next) =>
        {
            string spaPath = context.Request.Path.Value ?? "";
            if (SpaFrontend.Mode == "next"
                && (spaPath == "/" || spaPath == "/admin"))
            {
                context.Response.Redirect(spaPath == "/" ? "/app-next/site/" : "/app-next/admin/");
                return;
            }

            bool isSpa = spaPath.StartsWith("/app-next/", StringComparison.Ordinal)
                || spaPath == "/api/bootstrap"
                || ContractApi.Matches(spaPath);
            if (!isSpa)
            {
                await next();
                return;
            }

            async Task Terminal()
            {
                var decrypted = context.Items[Cookie.EncryptCookies.ContextKey]
                    as Support.PhpArray;
                string? cookieLocale = decrypted?.Get("locale") as string;
                string? lang = context.Request.Query["lang"].ToString();
                DetectLanguagePrefer.Result resolved = DetectLanguagePrefer.Resolve(
                    new DetectLanguagePrefer.Input(
                        string.IsNullOrEmpty(lang) ? null : lang,
                        cookieLocale,
                        context.Request.Headers.AcceptLanguage.ToString(),
                        null,
                        I18n.Locales.AppLocale));
                context.Items["locale"] = resolved.Locale;
                if (ContractApi.Matches(spaPath)
                    && await ContractApi.HandleAsync(context, users, notifications, reports,
                        textures, communicationsService, settings))
                {
                    return;
                }

                await SpaFrontend.HandleAsync(context, option, settings, users, paths);
            }

            async Task WithSession()
            {
                await new StartSessionMiddleware(_ => Terminal(), session,
                    SessionCookieConfig.Default, cookies).InvokeAsync(context);
            }

            if (cookies is not null)
            {
                await new EncryptCookiesMiddleware(_ => WithSession(), cookies)
                    .InvokeAsync(context);
            }
            else
            {
                await WithSession();
            }
        });

        app.Run(dispatcher.DispatchAsync);
        return app;
    }

    /// <summary><c>php artisan route:list --json --columns=uri,name</c> 的等价输出。</summary>
    public static string RouteListJson(Routes routes)
    {
        var body = new PhpArray();
        int index = 0;
        foreach (RouteDefinition route in routes.All)
        {
            var row = new PhpArray()
                .With("uri", route.Uri)
                .With("name", string.IsNullOrEmpty(route.Name) ? null : route.Name);
            body.Put(index++, row);
        }

        return PhpJson.Encode(body,
            PhpJson.JsonUnescapedUnicode | PhpJson.JsonUnescapedSlashes);
    }
}
