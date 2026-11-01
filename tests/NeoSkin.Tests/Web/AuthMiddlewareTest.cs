using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using NeoSkin.Web.Auth;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 认证中间件（auth / banned / email bind / guest / role / verified）的单元测试。
/// </summary>
public sealed class AuthMiddlewareTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-authmw-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly UserRepository _users;
    private readonly SessionStore _session;
    private readonly Option _option;
    private readonly TranslationFileLoader _loader = new();

    public AuthMiddlewareTest()
    {
        Directory.CreateDirectory(_dir);
        var settings = new NeoSkinSettings();
        settings.Database.Connection = "sqlite";
        settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        settings.Storage.Root = Path.Combine(_dir, "storage");
        _db = new Database(settings);
        var schema = new SchemaInstaller(_db.Open, settings);
        schema.Install();
        var paths = new AppPaths(_dir, settings);
        Directory.CreateDirectory(paths.Storage);
        File.WriteAllText(paths.InstallLock, "");

        _users = new UserRepository(_db);
        _option = new Option(_db, schema, paths);
        _session = SessionStore.Begin("BS_SESSION",
            new FileSessionHandler(Path.Combine(_dir, "sessions"), 120), null);
        _session.Start();
    }

    private int SeedUser(string email, int permission, bool verified = true)
    {
        int uid = _users.Insert(email, "N", 0, "h", "127.0.0.1", "Asia/Shanghai");
        _users.UpdatePermission(uid, permission);
        _users.UpdateVerified(uid, verified);
        return uid;
    }

    private int SeedAdmin()
    {
        int uid = SeedUser("admin@a.c", UserRepository.Admin);
        new SessionGuard(_session, _users).Login(uid);
        return uid;
    }

    private DefaultHttpContext Context(string path = "/user", string? accept = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Request.Host = new HostString("localhost");
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        context.Response.Body = new MemoryStream();
        context.Items[StartSessionMiddleware.SessionKey] = _session;
        context.Items["translator"] = new Translator(_loader, "zh_CN", "en");
        return context;
    }

    private static async Task<string> BodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static Func<Task> Next(HttpContext context, Action onNext) => () =>
    {
        onNext();
        return Task.CompletedTask;
    };

    // ================================================================== Authenticate

    [Fact]
    public async Task AuthenticateAllowsLoggedInUser()
    {
        SeedAdmin();
        var context = Context();
        bool nextCalled = false;

        await AuthMiddleware.Authenticate(_users, _loader)(context, "web",
            Next(context, () => nextCalled = true));

        Assert.True(nextCalled);
        Assert.Equal(UserRepository.Admin, AuthMiddleware.CurrentUser(context)!.Permission);
    }

    [Fact]
    public async Task AuthenticateDeniesJsonWith401()
    {
        var context = Context(accept: "application/json");
        bool nextCalled = false;

        await AuthMiddleware.Authenticate(_users, _loader)(context, "web",
            Next(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Contains("Unauthenticated.", await BodyAsync(context));
    }

    [Fact]
    public async Task AuthenticateRedirectsBrowserAndStoresIntendedPath()
    {
        var context = Context("/user/profile");
        bool nextCalled = false;

        await AuthMiddleware.Authenticate(_users, _loader)(context, "web",
            Next(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/auth/login", context.Response.Headers.Location.ToString());
        Assert.Equal("http://localhost/user/profile", _session.Get(AuthMiddleware.LastRequestedPath));

        _session.Save();
        SessionStore reloaded = SessionStore.Begin("BS_SESSION",
            new FileSessionHandler(Path.Combine(_dir, "sessions"), 120), _session.Id);
        reloaded.Start();
        Assert.Equal("http://localhost/user/profile", reloaded.Get(AuthMiddleware.LastRequestedPath));
    }

    // ================================================================== RejectBannedUser

    [Fact]
    public async Task BannedUserIsRejectedWithCodeMinusOne()
    {
        int uid = SeedUser("banned@a.c", UserRepository.Banned);
        new SessionGuard(_session, _users).Login(uid);

        var context = Context(accept: "application/json");
        await AuthMiddleware.Authenticate(_users, _loader)(context, "web", () => Task.CompletedTask);
        bool nextCalled = false;

        await AuthMiddleware.RejectBannedUser(_loader)(context, null,
            Next(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Contains("\"code\":-1", await BodyAsync(context));
    }

    [Fact]
    public async Task LogoutRouteSkipsBannedCheck()
    {
        int uid = SeedUser("banned2@a.c", UserRepository.Banned);
        new SessionGuard(_session, _users).Login(uid);

        var context = Context();
        context.Items["route.name"] = "auth.logout";
        await AuthMiddleware.Authenticate(_users, _loader)(context, "web", () => Task.CompletedTask);
        bool nextCalled = false;

        await AuthMiddleware.RejectBannedUser(_loader)(context, null,
            Next(context, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    // ================================================================== EnsureEmailFilled

    [Fact]
    public async Task EmailFilledRedirectsAwayFromBind()
    {
        SeedAdmin();
        var context = Context("/auth/bind");
        await AuthMiddleware.Authenticate(_users, _loader)(context, "web", () => Task.CompletedTask);
        bool nextCalled = false;

        await AuthMiddleware.EnsureEmailFilled()(context, null, Next(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal("/user", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task EmptyEmailRedirectsToBind()
    {
        int uid = SeedUser("noemail@a.c", UserRepository.Normal);
        _users.UpdateEmail(uid, "");
        new SessionGuard(_session, _users).Login(uid);

        var context = Context("/user");
        await AuthMiddleware.Authenticate(_users, _loader)(context, "web", () => Task.CompletedTask);
        bool nextCalled = false;

        await AuthMiddleware.EnsureEmailFilled()(context, null, Next(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal("/auth/bind", context.Response.Headers.Location.ToString());
    }

    // ================================================================== guest / role / verified

    [Fact]
    public async Task GuestRedirectsLoggedInUser()
    {
        SeedAdmin();
        var context = Context("/auth/login");
        bool nextCalled = false;

        await AuthMiddleware.RedirectIfAuthenticated()(context, null,
            Next(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal("/user", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task RoleThresholds()
    {
        SeedAdmin();
        var context = Context(accept: "application/json");
        await AuthMiddleware.Authenticate(_users, _loader)(context, "web", () => Task.CompletedTask);

        bool nextCalled = false;
        await AuthMiddleware.CheckRole()(context, "admin", Next(context, () => nextCalled = true));
        Assert.True(nextCalled);

        var superContext = Context(accept: "application/json");
        await AuthMiddleware.Authenticate(_users, _loader)(superContext, "web", () => Task.CompletedTask);
        await AuthMiddleware.CheckRole()(superContext, "super-admin", () => Task.CompletedTask);
        Assert.Equal(403, superContext.Response.StatusCode);
        Assert.Contains("\"message\":\"\"", await BodyAsync(superContext));
    }

    [Fact]
    public async Task RoleWithoutUserIsForbidden()
    {
        var context = Context(accept: "application/json");
        await AuthMiddleware.CheckRole()(context, "normal", () => Task.CompletedTask);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task VerificationRequirement()
    {
        _option.Set("require_verification", true);
        int uid = SeedUser("unverified@a.c", UserRepository.Normal, verified: false);
        new SessionGuard(_session, _users).Login(uid);

        var context = Context(accept: "application/json");
        await AuthMiddleware.Authenticate(_users, _loader)(context, "web", () => Task.CompletedTask);
        await AuthMiddleware.CheckUserVerified(_option)(context, null, () => Task.CompletedTask);
        Assert.Equal(403, context.Response.StatusCode);

        _option.Set("require_verification", false);
        bool nextCalled = false;
        await AuthMiddleware.CheckUserVerified(_option)(context, null,
            Next(context, () => nextCalled = true));
        Assert.True(nextCalled);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }
}
