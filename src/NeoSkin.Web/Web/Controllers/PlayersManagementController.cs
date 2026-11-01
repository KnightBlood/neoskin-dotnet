using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web.Pagination;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\PlayersManagementController</c> 的移植（web 与 api 共用）。
/// </summary>
public sealed class PlayersManagementController(
    Option option,
    PlayerRepository players,
    UserRepository users,
    TextureRepository textures,
    Config.NeoSkinSettings settings)
{
    private const int PerPage = 10;

    /// <summary><c>GET admin/players/list</c> / <c>GET api/admin/players</c>。</summary>
    public RequestHandler List() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string q = RequestInput.String(input, "q");
        int page = Math.Max(1, RequestInput.Int(input, "page"));

        SearchString.Result compiled = SearchString.Compile(q, PlayerSearchOptions());
        PlayerRepository.AdminPageResult result = players.AdminPage(compiled, page, PerPage);

        string path = "http://" + HostOf(context) + (context.Request.Path.Value ?? "/");
        Dictionary<string, object?> body = Paginator.Body(path, page, PerPage, result.Total);
        body["data"] = result.Rows;
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>PUT admin/players/{player}/name</c>。</summary>
    public RequestHandler Name() => async (context, route, parameters) =>
    {
        PlayerRow? player = await GuardAsync(context, parameters);
        if (player is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("player_name", "required");
        v.AddRule("player_name", new PlayerNameRule(option, Translator(context), Locale(context)));
        v.AddRule("player_name", "min:" + option.GetInt("player_name_length_min", 3));
        v.AddRule("player_name", "max:" + option.GetInt("player_name_length_max", 16));
        v.AddRule("player_name", "unique:players,name");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string newName = RequestInput.String(input, "player_name");
        players.UpdateName(player.Pid, newName, PhpDate.Now(settings.Timezone));
        await JsonAsync(context, 200, Message(0,
            Trans(context, "admin.players.name.success",
                new Dictionary<string, object?> { ["player"] = newName })));
    };

    /// <summary><c>PUT admin/players/{player}/owner</c>。</summary>
    public RequestHandler Owner() => async (context, route, parameters) =>
    {
        PlayerRow? player = await GuardAsync(context, parameters);
        if (player is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("uid", "required");
        v.AddRule("uid", "integer");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        int uid = RequestInput.Int(input, "uid");
        UserRepository.UserRow? user = users.FindById(uid);
        if (user is null)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "admin.users.operations.non-existent")));
            return;
        }

        players.UpdateOwner(player.Pid, uid, PhpDate.Now(settings.Timezone));
        await JsonAsync(context, 200, Message(0,
            Trans(context, "admin.players.owner.success",
                new Dictionary<string, object?>
                {
                    ["player"] = player.Name,
                    ["user"] = user.Nickname ?? "",
                })));
    };

    /// <summary><c>PUT admin/players/{player}/textures</c>（tid + type=skin|cape）。</summary>
    public RequestHandler Texture() => async (context, route, parameters) =>
    {
        PlayerRow? player = await GuardAsync(context, parameters);
        if (player is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("tid", "required");
        v.AddRule("tid", "integer");
        v.AddRule("type", "required");
        v.AddRule("type", "in:skin,cape");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        int tid = RequestInput.Int(input, "tid");
        string type = RequestInput.String(input, "type");

        if (tid != 0 && textures.FindByTid(tid) is null)
        {
            await JsonAsync(context, 200, Message(1,
                Trans(context, "admin.players.textures.non-existent",
                    new Dictionary<string, object?> { ["tid"] = tid })));
            return;
        }

        int tidSkin = player.TidSkin;
        int tidCape = player.TidCape;
        if (type == "skin")
        {
            tidSkin = tid;
        }
        else
        {
            tidCape = tid;
        }

        players.UpdateTextures(player.Pid, tidSkin, tidCape, PhpDate.Now(settings.Timezone));
        await JsonAsync(context, 200, Message(0,
            Trans(context, "admin.players.textures.success",
                new Dictionary<string, object?> { ["player"] = player.Name })));
    };

    /// <summary><c>DELETE admin/players/{player}</c>。</summary>
    public RequestHandler Delete() => async (context, route, parameters) =>
    {
        PlayerRow? player = await GuardAsync(context, parameters);
        if (player is null)
        {
            return;
        }

        players.Delete(player.Pid);
        await JsonAsync(context, 200, Message(0, Trans(context, "admin.players.delete.success")));
    };

    // ================================================================== 辅助

    /// <summary>隐式绑定 + 权限闭包：拥有者权限不低于当前管理员 → 403。</summary>
    private async Task<PlayerRow?> GuardAsync(HttpContext context,
        IReadOnlyDictionary<string, string> parameters)
    {
        int pid = parameters.TryGetValue("player", out string? raw)
            ? RequestInput.ToInt(raw)
            : 0;
        PlayerRow? player = players.FindById(pid);
        if (player is null)
        {
            context.Response.StatusCode = 404;
            return null;
        }

        UserRepository.UserRow? owner = users.FindById(player.Uid);
        if (owner is null)
        {
            context.Response.StatusCode = 500;
            return null;
        }

        UserRepository.UserRow? auth = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (auth is not null && owner.Uid != auth.Uid && owner.Permission >= auth.Permission)
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "admin.players.no-permission")));
            return null;
        }

        return player;
    }

    private static SearchString.Options PlayerSearchOptions() =>
        new SearchString.Options()
            .Column("pid")
            .Column("uid")
            .Column("tid_skin")
            .Column("tid_cape")
            .Alias("skin", "tid_skin")
            .Alias("cape", "tid_cape")
            .Searchable("name")
            .Date("last_modified")
            .Keyword("sort", "order_by")
            .Keyword("fields", "select")
            .Keyword("limit", "limit")
            .Keyword("from", "offset");

    private UserRepository.UserRow? SessionUser(HttpContext context)
    {
        Session.SessionStore? session =
            context.Items[StartSessionMiddleware.SessionKey] as Session.SessionStore;
        return session is null ? null : new Auth.SessionGuard(session, users).User();
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string HostOf(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

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
