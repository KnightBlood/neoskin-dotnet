using System.Security.Cryptography;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Auth;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Storage;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web.Pagination;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\SkinlibController</c> 的移植。
///
/// <p>已实现：<c>library</c>（分页）、<c>info</c>、<c>delete</c>、<c>privacy</c>、
/// <c>rename</c>、<c>type</c>。<c>show</c> / <c>upload</c> / <c>handleUpload</c>
/// 依赖模板与图片处理，后续补齐。</p>
/// </summary>
public sealed class SkinlibController(
    Option option,
    TextureRepository textures,
    UserRepository users,
    PlayerRepository players,
    ClosetRepository closet,
    Database db,
    string texturesDirectory,
    Config.NeoSkinSettings settings)
{
    private readonly ITextureStorage _storage = new LocalTextureStorage(texturesDirectory);

    /// <summary><c>GET skinlib/list</c>：LengthAwarePaginator JSON。</summary>
    public RequestHandler Library() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string type = RequestInput.String(input, "filter") is { Length: > 0 } f ? f : "skin";
        string? keyword = RequestInput.String(input, "keyword") is { Length: > 0 } k ? k : null;
        int? uploader = int.TryParse(RequestInput.String(input, "uploader"), out int u) ? u : null;
        string sort = RequestInput.String(input, "sort") is { Length: > 0 } s ? s : "time";
        int page = Math.Max(1, RequestInput.Int(input, "page"));
        const int perPage = 20;

        UserRepository.UserRow? viewer = CurrentUser(context);
        int? viewerUid = viewer?.Uid;
        bool admin = viewer?.IsAdmin == true;

        long total = textures.CountForLibrary(type, keyword, uploader, viewerUid, admin);
        long last = Math.Max(1, (total + perPage - 1) / perPage);
        long offset = (long)(page - 1) * perPage;
        List<Dictionary<string, object?>> rows = textures.PageForLibrary(type, keyword,
            uploader, viewerUid, admin, sort, perPage, (int)offset);

        string path = "http://" + Host(context) + "/skinlib/list";
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["current_page"] = page,
            ["data"] = rows,
            ["first_page_url"] = PageUrl(path, 1),
            ["from"] = rows.Count == 0 ? null : offset + 1,
            ["last_page"] = last,
            ["last_page_url"] = PageUrl(path, (int)last),
            ["links"] = Paginator.Links(path, page, (int)last),
            ["next_page_url"] = page < last ? PageUrl(path, page + 1) : null,
            ["path"] = path,
            ["per_page"] = perPage,
            ["prev_page_url"] = page > 1 ? PageUrl(path, page - 1) : null,
            ["to"] = rows.Count == 0 ? null : offset + rows.Count,
            ["total"] = total,
        };

        await JsonAsync(context, 200, body);
    };

    /// <summary><c>GET skinlib/info/{texture}</c> / <c>GET texture/{texture}</c>。</summary>
    public RequestHandler Info() => async (context, route, parameters) =>
    {
        long tid = ToLong(parameters, "texture");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        UserRepository.UserRow? viewer = CurrentUser(context);
        if (!texture.IsPublic)
        {
            bool allowed = viewer is not null
                && (viewer.Uid == texture.Uploader || viewer.IsAdmin);
            if (!allowed)
            {
                int status = option.GetInt("status_code_for_private", 403);
                context.Response.StatusCode = status == 404 ? 404 : 403;
                return;
            }
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tid"] = texture.Tid,
            ["name"] = texture.Name,
            ["type"] = texture.Type,
            ["hash"] = texture.Hash,
            ["size"] = texture.Size,
            ["uploader"] = texture.Uploader,
            ["public"] = texture.IsPublic,
            ["upload_at"] = texture.UploadAt,
            ["likes"] = texture.Likes,
        };
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>DELETE texture/{texture}</c>。</summary>
    public RequestHandler Delete() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long tid = ToLong(parameters, "texture");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (!OwnedBy(user, texture))
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "skinlib.no-permission")));
            return;
        }

        if (textures.CountByHash(texture.Hash) == 1)
        {
            _storage.Delete(texture.Hash);
        }

        textures.DeleteByTid(tid);
        TextureSideEffects.OnDeleted(db, option, texture, users, players, closet, textures);

        await JsonAsync(context, 200, Message(0, Trans(context, "skinlib.delete.success")));
    };

    /// <summary><c>PUT texture/{texture}/privacy</c>。</summary>
    public RequestHandler Privacy() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long tid = ToLong(parameters, "texture");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (!OwnedBy(user, texture))
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "skinlib.no-permission")));
            return;
        }

        UserRepository.UserRow? uploader = users.FindById(texture.Uploader);
        if (uploader is null)
        {
            context.Response.StatusCode = 500;
            return;
        }

        int publicScore = RequestInput.ToInt(option.Get("score_per_storage"));
        int privateScore = option.GetInt("private_score_per_storage", 10);
        int scoreDiff = texture.Size == 0
            ? 0
            : (int)(texture.Size * (privateScore - publicScore) * (texture.IsPublic ? -1 : 1));
        if (texture.IsPublic
            && RequestInput.ToBool(option.Get("take_back_scores_after_deletion", true)))
        {
            scoreDiff -= option.GetInt("score_award_per_texture", 0);
        }

        if (uploader.Score + scoreDiff < 0)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "skinlib.upload.lack-score")));
            return;
        }

        if (!texture.IsPublic)
        {
            TextureRow? duplicate = textures.FindPublicByHash(texture.Hash);
            if (duplicate is not null)
            {
                var body = Message(2, Trans(context, "skinlib.upload.repeated"));
                body["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tid"] = duplicate.Tid,
                };
                await JsonAsync(context, 200, body);
                return;
            }
        }

        users.UpdateScore(uploader.Uid, uploader.Score + scoreDiff);
        textures.UpdatePublic(tid, !texture.IsPublic);
        TextureRow? updated = textures.FindByTid(tid);
        if (updated is not null)
        {
            TextureSideEffects.OnPrivacyUpdated(db, option, updated, users, players, closet,
                textures);
        }

        await JsonAsync(context, 200, Message(0,
            Trans(context, "skinlib.privacy.success",
                new Dictionary<string, object?>
                {
                    ["privacy"] = Trans(context, updated is { IsPublic: true }
                        ? "general.public" : "general.private"),
                })));
    };

    /// <summary><c>PUT texture/{texture}/name</c>。</summary>
    public RequestHandler Rename() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long tid = ToLong(parameters, "texture");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (!OwnedBy(user, texture))
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "skinlib.no-permission")));
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        string regexp = option.GetString("texture_name_regexp", null) ?? "";
        v.AddRule("name", "required");
        v.AddRule("name", regexp.Length == 0 ? "string" : "regex:" + regexp);
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string name = RequestInput.String(input, "name");
        textures.UpdateName(tid, name);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "skinlib.rename.success",
                new Dictionary<string, object?> { ["name"] = name })));
    };

    /// <summary><c>PUT texture/{texture}/type</c>。</summary>
    public RequestHandler Type() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long tid = ToLong(parameters, "texture");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (!OwnedBy(user, texture))
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "skinlib.no-permission")));
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("type", "required");
        v.AddRule("type", "in:steve,alex,cape");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string type = RequestInput.String(input, "type");
        textures.UpdateType(tid, type);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "skinlib.model.success",
                new Dictionary<string, object?> { ["model"] = type })));
    };

    // ================================================================== 视图

    /// <summary><c>GET skinlib/show/{texture}</c>：材质详情页。</summary>
    public RequestHandler Show() => async (context, route, parameters) =>
    {
        long tid = ToLong(parameters, "texture");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        UserRepository.UserRow? viewer = CurrentUser(context);
        if (!texture.IsPublic)
        {
            bool allowed = viewer is not null
                && (viewer.Uid == texture.Uploader || viewer.IsAdmin);
            if (!allowed)
            {
                int status = option.GetInt("status_code_for_private", 403);
                context.Response.StatusCode = status == 404 ? 404 : 403;
                return;
            }
        }

        if (!_storage.Exists(texture.Hash))
        {
            if (option.GetBoolean("auto_del_invalid_texture", false))
            {
                textures.DeleteByTid(texture.Tid);
            }

            context.Response.StatusCode = 404;
            return;
        }

        UserRepository.UserRow? uploader = users.FindById(texture.Uploader);
        bool admin = viewer?.IsAdmin == true;
        var extra = new PhpArray()
            .With("download", option.GetBoolean("allow_downloading_texture", true))
            .With("currentUid", viewer?.Uid ?? 0)
            .With("admin", admin)
            .With("inCloset", viewer is not null && users.InCloset(viewer.Uid, (int)texture.Tid))
            .With("uploaderExists", uploader is not null)
            .With("nickname", uploader?.Nickname
                ?? Trans(context, "general.unexistent-user"))
            .With("report", option.GetInt("reporter_score_modification", 0))
            .With("badges", Badges(uploader));

        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            viewer, Locale(context), host, context.Request.Path.Value ?? "/skinlib/show/" + tid,
            settings.Debug, "skinlib", session);
        vars["pageTitle"] = texture.Name;
        vars["errors"] = TemplateErrors.Empty;
        vars["gridRows"] = PageView.Grid(new List<object?>
        {
            PageView.Column("md-8", "shared/previewer"),
            PageView.Column("md-4", "skinlib/widgets/show/side"),
        });
        vars["extra_json"] = PhpJson.Encode(extra, 0);
        string html = TemplateRenderer.Render("skinlib/show", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };

    /// <summary><c>GET skinlib/upload</c>：上传页。</summary>
    public RequestHandler Upload() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string locale = Locale(context);
        string regexp = option.GetString("texture_name_regexp", null) ?? "";
        string rule = regexp.Length == 0
            ? Trans(context, "skinlib.upload.name-rule")
            : Trans(context, "skinlib.upload.name-rule-regexp",
                new Dictionary<string, object?> { ["regexp"] = regexp });
        var extra = new PhpArray()
            .With("rule", rule)
            .With("privacyNotice", Trans(context, "skinlib.upload.private-score-notice",
                new Dictionary<string, object?>
                {
                    ["score"] = option.GetInt("private_score_per_storage", 10).ToString(),
                }))
            .With("score", user.Score)
            .With("scorePublic", RequestInput.ToInt(option.Get("score_per_storage")))
            .With("scorePrivate", option.GetInt("private_score_per_storage", 10))
            .With("closetItemCost", option.GetInt("score_per_closet_item", 0))
            .With("award", option.GetInt("score_award_per_texture", 0))
            .With("contentPolicy", PluginFilters.ApplyString("content_policy",
                MarkdownLite.Render(option.GetLocalized("content_policy", "")?.ToString() ?? ""),
                PluginHost.PluginRuntime.Current));

        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, locale, host, context.Request.Path.Value ?? "/skinlib/upload",
            settings.Debug, "skinlib", session);
        vars["pageTitle"] = Trans(context, "skinlib.upload.title");
        vars["errors"] = TemplateErrors.Empty;
        vars["gridRows"] = PageView.Grid(new List<object?>
        {
            PageView.Column("md-6", "skinlib/widgets/upload/input"),
            PageView.Column("md-6", "shared/previewer"),
        });
        vars["extra_json"] = PhpJson.Encode(extra, 0);
        string html = TemplateRenderer.Render("skinlib/upload", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };
    // ================================================================== 辅助

    /// <summary><c>POST texture</c> / <c>POST skinlib</c>：上传纹理。</summary>
    public RequestHandler HandleUpload() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        IFormFile? file = null;
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            file = form.Files.GetFile("file");
        }

        byte[] bytes = [];
        if (file is not null)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        // Laravel 的输入里 file 是 UploadedFile 对象；这里放非空占位，
        // 让自定义的文件规则（非隐式 → 值为 null 时会被跳过）正常参与校验。
        input["file"] = file;

        var v = new Validator(input, Translator(context), Locale(context), option, null);
        string regexp = option.GetString("texture_name_regexp", null) ?? "";
        v.AddRule("name", "required");
        v.AddRule("name", regexp.Length == 0 ? "string" : "regex:" + regexp);
        v.AddRule("type", "required");
        v.AddRule("type", "in:steve,alex,cape");
        v.AddRule("public", "required");
        v.AddRule("public", "boolean");
        v.AddRule("file", new FileRequiredRule(file, Translator(context), Locale(context)));
        v.AddRule("file", new MimesPngRule(bytes, Translator(context), Locale(context)));
        v.AddRule("file", new FileMaxSizeRule(bytes.Length,
            option.GetInt("max_upload_file_size", 1024), Translator(context), Locale(context)));

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string name = RequestInput.String(input, "name");
        string type = RequestInput.String(input, "type");
        bool isPublic = RequestInput.ToBool(input.GetValueOrDefault("public"));

        PngSanitizer.Size? size = PngSanitizer.ReadSize(bytes) ?? new PngSanitizer.Size(0, 0);
        int width = size.Value.Width;
        int height = size.Value.Height;
        int maxWidth = option.GetInt("max_texture_width", 8192);
        if (width > maxWidth)
        {
            await JsonAsync(context, 200, Message(1,
                Trans(context, "skinlib.upload.too-wide",
                    new Dictionary<string, object?>
                    {
                        ["width"] = width.ToString(), ["maxWidth"] = maxWidth.ToString(),
                    })));
            return;
        }

        if (width % 64 != 0 || height % 32 != 0)
        {
            await JsonAsync(context, 200, Message(1, InvalidSize(context, type, width, height)));
            return;
        }

        double ratio = (double)width / height;
        if (type is "steve" or "alex")
        {
            if ((ratio != 2 && ratio != 1) || (type == "alex" && ratio == 2))
            {
                await JsonAsync(context, 200,
                    Message(1, InvalidSize(context, "skin", width, height)));
                return;
            }
        }
        else if (type == "cape" && ratio != 2)
        {
            await JsonAsync(context, 200,
                Message(1, InvalidSize(context, "cape", width, height)));
            return;
        }

        byte[]? sanitized = PngSanitizer.Sanitize(bytes);
        if (sanitized is null)
        {
            await JsonAsync(context, 200, Message(1, InvalidSize(context, type, width, height)));
            return;
        }

        string hash = Sha256Hex(sanitized);
        TextureRow? duplicated = textures.FindPublicOrMine(hash, user.Uid);
        if (duplicated is not null)
        {
            var body = Message(2, Trans(context, "skinlib.upload.repeated"));
            body["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tid"] = duplicated.Tid,
            };
            await JsonAsync(context, 200, body);
            return;
        }

        long fileSize = (sanitized.Length + 1023) / 1024;
        int cost = (int)fileSize * (isPublic
            ? RequestInput.ToInt(option.Get("score_per_storage"))
            : option.GetInt("private_score_per_storage", 10));
        cost += option.GetInt("score_per_closet_item", 0);
        cost -= option.GetInt("score_award_per_texture", 0);
        if (user.Score < cost)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.upload.lack-score")));
            return;
        }

        long newTid = textures.Insert(name, type, hash, fileSize, user.Uid, isPublic,
            PhpDate.Now("Asia/Shanghai"));
        if (!_storage.Exists(hash))
        {
            _storage.Write(hash, sanitized);
        }

        users.UpdateScore(user.Uid, user.Score - cost);
        closet.Attach(user.Uid, newTid, name);

        var response = Message(0, Trans(context, "skinlib.upload.success",
            new Dictionary<string, object?> { ["name"] = name }));
        response["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tid"] = newTid,
        };
        await JsonAsync(context, 200, response);
    };

    // ================================================================== 辅助

    private static PhpArray Badges(UserRepository.UserRow? uploader)
    {
        var badges = new PhpArray();
        if (uploader?.IsAdmin == true)
        {
            badges.Put(0, new PhpArray().With("text", "STAFF").With("color", "primary"));
        }

        return badges;
    }

    private static string InvalidSize(HttpContext context, string type, int width, int height)
    {
        string typeName = type == "cape"
            ? Trans(context, "general.cape")
            : Trans(context, "general.skin");
        return Trans(context, "skinlib.upload.invalid-size",
            new Dictionary<string, object?>
            {
                ["type"] = typeName,
                ["width"] = width.ToString(),
                ["height"] = height.ToString(),
            });
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary><c>'file' =&gt; required</c>。</summary>
    private sealed class FileRequiredRule(IFormFile? file, Translator translator, string locale)
        : IRule
    {
        public string? Name => null;

        public IReadOnlyList<string> Parameters => [];

        public bool Implicit => true;

        public string? Message => translator.Trans("validation.required",
            new Dictionary<string, object?> { ["attribute"] = "file" }, locale);

        public bool Passes(string attribute, object? value, Validator validator) =>
            file is not null && file.Length >= 0;
    }

    /// <summary><c>mimes:png</c>（按文件内容嗅探）。</summary>
    private sealed class MimesPngRule(byte[] bytes, Translator translator, string locale) : IRule
    {
        public string? Name => null;

        public IReadOnlyList<string> Parameters => [];

        public bool Implicit => true;

        public string? Message => translator.Trans("validation.mimes",
            new Dictionary<string, object?>
            {
                ["attribute"] = "file", ["values"] = "png",
            }, locale);

        public bool Passes(string attribute, object? value, Validator validator) =>
            PngSanitizer.IsPng(bytes);
    }

    /// <summary><c>max:&lt;KB&gt;</c>（对文件按 KB 比较）。</summary>
    private sealed class FileMaxSizeRule(long bytes, int maxKb, Translator translator,
        string locale) : IRule
    {
        public string? Name => null;

        public IReadOnlyList<string> Parameters => [];

        public bool Implicit => true;

        public string? Message => translator.Trans("validation.max.file",
            new Dictionary<string, object?>
            {
                ["attribute"] = "file", ["max"] = maxKb.ToString(),
            }, locale);

        public bool Passes(string attribute, object? value, Validator validator) =>
            (bytes + 1023) / 1024 <= maxKb;
    }

    private static bool OwnedBy(UserRepository.UserRow user, TextureRow texture) =>
        texture.Uploader == user.Uid || user.IsAdmin;

    /// <summary>
    /// 当前用户：优先取中间件写入的 <c>Items</c>（api/auth 路由），否则回退到会话
    /// ——<c>skinlib/list</c>、<c>skinlib/info</c> 只有 <c>web</c> 组，不会触发
    /// <c>auth:web</c>，源实现直接读 <c>Auth::user()</c>。
    /// </summary>
    private UserRepository.UserRow? CurrentUser(HttpContext context)
    {
        if (AuthMiddleware.CurrentUser(context) is { } current)
        {
            return current;
        }

        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        return session is null ? null : new SessionGuard(session, users).User();
    }

    private static long ToLong(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out string? value) && long.TryParse(value, out long parsed)
            ? parsed
            : 0;

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static string PageUrl(string path, int page) => path + "?page=" + page;

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;

    private static string Trans(HttpContext context, string key,
        IReadOnlyDictionary<string, object?> replace) =>
        Translator(context).Trans(key, replace) ?? key;

    private static Dictionary<string, object?> Message(int code, string message) =>
        new(StringComparer.Ordinal) { ["code"] = code, ["message"] = message };

    private static async Task ValidationFailedAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        await JsonAsync(context, 422, new Dictionary<string, object?>
        {
            ["message"] = Validator.Summarize(errors),
            ["errors"] = errors,
        });
    }

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}