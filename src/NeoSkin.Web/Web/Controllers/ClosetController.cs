using NeoSkin.Web.Db;
using NeoSkin.Web.Event;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.Web;
using NeoSkin.Web.View;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web.Pagination;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\ClosetController</c> 的移植。
///
/// <p>JSON actions：<c>getClosetData</c>（LengthAwarePaginator）、<c>allIds</c>、
/// <c>add</c> / <c>rename</c> / <c>remove</c>。<c>index</c>（HTML）待模板引擎。</p>
/// </summary>
public sealed class ClosetController(
    Option option,
    ClosetRepository closet,
    TextureRepository textures,
    UserRepository users,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>GET user/closet/list</c> / <c>GET api/closet</c>：分页 JSON。</summary>
    public RequestHandler GetClosetData() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string category = RequestInput.String(input, "category") is { Length: > 0 } c ? c : "skin";
        string? search = RequestInput.String(input, "q") is { Length: > 0 } q ? q : null;
        int page = Math.Max(1, RequestInput.Int(input, "page"));
        int perPage = RequestInput.Int(input, "perPage") is > 0 and int pp ? pp : 6;

        long total = closet.Count(user.Uid, category, search);
        long last = Math.Max(1, (total + perPage - 1) / perPage);
        long offset = (long)(page - 1) * perPage;

        List<ClosetRepository.Item> items =
            closet.Page(user.Uid, category, search, perPage, (int)offset);
        var rows = new List<Dictionary<string, object?>>();
        foreach (ClosetRepository.Item item in items)
        {
            Dictionary<string, object?> row = ClosetRepository.TextureJson(item.Texture);
            row["pivot"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user_uid"] = item.UserUid,
                ["texture_tid"] = item.TextureTid,
                ["item_name"] = item.ItemName,
            };
            rows.Add(row);
        }

        string path = "http://" + Host(context) + (context.Request.Path.Value ?? "/");
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

    /// <summary><c>GET user/closet/ids</c>：<c>pluck('texture_tid')</c>。</summary>
    public RequestHandler AllIds() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        await JsonAsync(context, 200, closet.AllIds(user.Uid));
    };

    /// <summary><c>POST user/closet</c> / <c>POST api/closet</c>。</summary>
    public RequestHandler Add() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("tid", "required");
        v.AddRule("tid", "integer");
        v.AddRule("name", "required");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        long tid = RequestInput.Int(input, "tid");
        string name = RequestInput.String(input, "name");

        int cost = option.GetInt("score_per_closet_item", 0);
        if (user.Score < cost)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "user.closet.add.lack-score")));
            return;
        }

        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "user.closet.add.not-found")));
            return;
        }

        if (!texture.IsPublic && texture.Uploader != user.Uid && !user.IsAdmin)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.show.private")));
            return;
        }

        if (closet.Exists(user.Uid, tid))
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "user.closet.add.repeated")));
            return;
        }

        closet.Attach(user.Uid, tid, name);
        users.UpdateScore(user.Uid, user.Score - cost);
        textures.UpdateLikes(tid, texture.Likes + 1);

        UserRepository.UserRow? uploader = users.FindById(texture.Uploader);
        int award = option.GetInt("score_award_per_like", 0);
        if (uploader is not null && uploader.Uid != user.Uid && award != 0)
        {
            users.UpdateScore(uploader.Uid, uploader.Score + award);
        }

        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.closet.add.success",
                new Dictionary<string, object?> { ["name"] = name })));
    };

    /// <summary><c>PUT user/closet/{tid}</c> / <c>PUT api/closet/{tid}</c>。</summary>
    public RequestHandler Rename() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("name", "required");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string name = RequestInput.String(input, "name");
        long tid = parameters.TryGetValue("tid", out string? raw) ? RequestInput.ToInt(raw) : 0;

        ClosetRepository.Item? item = closet.Find(user.Uid, tid);
        if (item is null)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.closet.remove.non-existent")));
            return;
        }

        closet.RenameItem(user.Uid, tid, name);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.closet.rename.success",
                new Dictionary<string, object?> { ["name"] = name })));
    };

    /// <summary><c>DELETE user/closet/{tid}</c> / <c>DELETE api/closet/{tid}</c>。</summary>
    public RequestHandler Remove() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long tid = parameters.TryGetValue("tid", out string? raw) ? RequestInput.ToInt(raw) : 0;

        ClosetRepository.Item? item = closet.Find(user.Uid, tid);
        if (item is null)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.closet.remove.non-existent")));
            return;
        }

        closet.Detach(user.Uid, tid);
        if (RequestInput.ToBool(option.Get("return_score", true)))
        {
            users.UpdateScore(user.Uid,
                user.Score + option.GetInt("score_per_closet_item", 0));
        }

        TextureRow? texture = textures.FindByTid(tid);
        if (texture is not null)
        {
            textures.UpdateLikes(tid, texture.Likes - 1);
            EventRuntime.Fire("closet.removed", texture, user.Uid);
            UserRepository.UserRow? uploader = users.FindById(texture.Uploader);
            int award = option.GetInt("score_award_per_like", 0);
            if (uploader is not null && award != 0)
            {
                users.UpdateScore(uploader.Uid, uploader.Score - award);
            }
        }

        await JsonAsync(context, 200, Message(0, Trans(context, "user.closet.remove.success")));
    };

    /// <summary><c>GET user/closet</c>：衣柜页。</summary>
    public RequestHandler Index() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string locale = Locale(context);
        string rule = option.GetString("player_name_rule", "official") ?? "official";
        var extra = new PhpArray()
            .With("unverified", option.GetBoolean("require_verification", false) && !user.Verified)
            .With("rule", Trans(context, "user.player.player-name-rule." + rule))
            .With("length", Trans(context, "user.player.player-name-length",
                new Dictionary<string, object?>
                {
                    ["min"] = option.GetInt("player_name_length_min", 3).ToString(),
                    ["max"] = option.GetInt("player_name_length_max", 16).ToString(),
                }));

        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, locale, host, context.Request.Path.Value ?? "/user/closet", settings.Debug,
            "user", session);
        vars["pageTitle"] = Trans(context, "general.my-closet");
        vars["errors"] = TemplateErrors.Empty;
        vars["gridRows"] = PageView.Grid(new List<object?>
        {
            PageView.Column("md-8", "user/widgets/email-verification",
                "user/widgets/closet/list"),
            PageView.Column("md-4", "shared/previewer"),
        });
        vars["extra_json"] = PhpJson.Encode(extra, 0);
        string html = TemplateRenderer.Render("user/closet", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };
    // ================================================================== 辅助

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
