using NeoSkin.Web.Db;
using NeoSkin.Web.Event;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\PlayerController</c> 的移植。
///
/// <p>JSON actions：<c>list / add / delete / rename / setTexture / clearTexture</c>。
/// <c>index</c>（HTML 视图）等模板引擎（Phase 7）接入后补齐。</p>
/// </summary>
public sealed class PlayerController(
    Option option,
    PlayerRepository players,
    TextureRepository textures,
    UserRepository users,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>GET user/player/list</c> / <c>GET api/players</c>。</summary>
    public RequestHandler List() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var output = new List<Dictionary<string, object?>>();
        foreach (PlayerRow row in players.ListByUid(user.Uid))
        {
            EventRuntime.Fire(new Events.PlayerRetrieved(row));
            output.Add(PlayerJson(row.Pid, row.Uid, row.Name, row.TidCape,
                players.LastModified(row.Pid), row.TidSkin));
        }

        await JsonAsync(context, 200, output);
    };

    /// <summary><c>POST user/player</c> / <c>POST api/players</c>。</summary>
    public RequestHandler Add() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("name", "required");
        v.AddRule("name", new PlayerNameRule(option, Translator(context), Locale(context)));
        v.AddRule("name", "min:" + option.GetInt("player_name_length_min", 3));
        v.AddRule("name", "max:" + option.GetInt("player_name_length_max", 16));
        v.AddRule("name", "unique:players");

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string name = RequestInput.String(input, "name");
        int cost = option.GetInt("score_per_player", 100);
        if (user.Score < cost)
        {
            await JsonAsync(context, 200, Message(7, Trans(context, "user.player.add.lack-score")));
            return;
        }

        EventRuntime.Fire(new Events.PlayerWillBeAdded(name));
        int pid = players.Insert(user.Uid, name, PhpDate.Now(Timezone));
        users.UpdateScore(user.Uid, user.Score - cost);

        PlayerRow? created = players.FindById(pid);
        if (created is not null)
        {
            EventRuntime.Fire(new Events.PlayerWasAdded(created));
        }

        var data = PlayerJson(pid, user.Uid, name, 0, players.LastModified(pid), -1);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.player.add.success",
                new Dictionary<string, object?> { ["name"] = name }), data));
    };

    /// <summary><c>DELETE user/player/{player}</c>。</summary>
    public RequestHandler Delete() => async (context, route, parameters) =>
    {
        (UserRepository.UserRow? user, PlayerRow? player) = await OwnedPlayerAsync(context, parameters);
        if (user is null || player is null)
        {
            return;
        }

        EventRuntime.Fire(new Events.PlayerWillBeDeleted(player));
        string name = player.Name;
        players.Delete(player.Pid);
        EventRuntime.Fire(new Events.PlayerWasDeleted(name));

        if (RequestInput.ToBool(option.Get("return_score", true)))
        {
            users.UpdateScore(user.Uid, user.Score + option.GetInt("score_per_player", 100));
        }

        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.player.delete.success",
                new Dictionary<string, object?> { ["name"] = name })));
    };

    /// <summary><c>PUT user/player/{player}/name</c>。</summary>
    public RequestHandler Rename() => async (context, route, parameters) =>
    {
        (UserRepository.UserRow? user, PlayerRow? player) = await OwnedPlayerAsync(context, parameters);
        if (user is null || player is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("name", "required");
        v.AddRule("name", new PlayerNameRule(option, Translator(context), Locale(context)));
        v.AddRule("name", "min:" + option.GetInt("player_name_length_min", 3));
        v.AddRule("name", "max:" + option.GetInt("player_name_length_max", 16));

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string name = RequestInput.String(input, "name");
        PlayerRow? duplicated = players.FindByName(name);
        if (duplicated is not null && duplicated.Pid != player.Pid)
        {
            await ValidationFailedAsync(context, new Dictionary<string, List<string>>
            {
                ["name"] = [Trans(context, "validation.unique",
                    new Dictionary<string, object?> { ["attribute"] = "name" })],
            });
            return;
        }

        players.UpdateName(player.Pid, name, PhpDate.Now(Timezone));

        PlayerRow? renamed = players.FindById(player.Pid);
        if (renamed is not null)
        {
            EventRuntime.Fire(new Events.PlayerProfileUpdated(renamed));
        }

        var data = PlayerJson(player.Pid, player.Uid, name, player.TidCape,
            players.LastModified(player.Pid), player.TidSkin);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.player.rename.success",
                new Dictionary<string, object?> { ["old"] = player.Name, ["new"] = name }), data));
    };

    /// <summary><c>PUT user/player/{player}/textures</c>。</summary>
    public RequestHandler SetTexture() => async (context, route, parameters) =>
    {
        (UserRepository.UserRow? user, PlayerRow? player) = await OwnedPlayerAsync(context, parameters);
        if (user is null || player is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        int tidSkin = player.TidSkin;
        int tidCape = player.TidCape;

        foreach (string type in new[] { "skin", "cape" })
        {
            object? raw = RequestInput.Input(input, type);
            if (raw is null || RequestInput.ToInt(raw) == 0)
            {
                continue;
            }

            int tid = RequestInput.ToInt(raw);
            TextureRow? texture = textures.FindByTid(tid);
            if (texture is null)
            {
                await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.non-existent")));
                return;
            }

            if (!users.InCloset(user.Uid, tid))
            {
                await JsonAsync(context, 200,
                    Message(1, Trans(context, "user.closet.remove.non-existent")));
                return;
            }

            if (type == "skin")
            {
                tidSkin = tid;
            }
            else
            {
                tidCape = tid;
            }
        }

        players.UpdateTextures(player.Pid, tidSkin, tidCape, PhpDate.Now(Timezone));

        PlayerRow? updated = players.FindById(player.Pid);
        if (updated is not null)
        {
            EventRuntime.Fire(new Events.PlayerProfileUpdated(updated));
        }

        var data = PlayerJson(player.Pid, player.Uid, player.Name, tidCape,
            players.LastModified(player.Pid), tidSkin);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.player.set.success",
                new Dictionary<string, object?> { ["name"] = player.Name }), data));
    };

    /// <summary><c>DELETE user/player/{player}/textures</c>。</summary>
    public RequestHandler ClearTexture() => async (context, route, parameters) =>
    {
        (UserRepository.UserRow? user, PlayerRow? player) = await OwnedPlayerAsync(context, parameters);
        if (user is null || player is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        int tidSkin = player.TidSkin;
        int tidCape = player.TidCape;
        List<string> types = RequestInput.List(input, "type");

        if (RequestInput.Input(input, "skin") is not null || types.Contains("skin"))
        {
            tidSkin = 0;
        }

        if (RequestInput.Input(input, "cape") is not null || types.Contains("cape"))
        {
            tidCape = 0;
        }

        players.UpdateTextures(player.Pid, tidSkin, tidCape, PhpDate.Now(Timezone));

        PlayerRow? updated = players.FindById(player.Pid);
        if (updated is not null)
        {
            EventRuntime.Fire(new Events.PlayerProfileUpdated(updated));
        }

        var data = PlayerJson(player.Pid, player.Uid, player.Name, tidCape,
            players.LastModified(player.Pid), tidSkin);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.player.clear.success",
                new Dictionary<string, object?> { ["name"] = player.Name }), data));
    };

    /// <summary><c>GET user/player</c>：角色管理页。</summary>
    public RequestHandler Index() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var extra = new PhpArray()
            .With("count", players.CountByUser(user.Uid))
            .With("rule", Trans(context, "user.player.player-name-rule."
                + option.GetString("player_name_rule", "official")))
            .With("length", Trans(context, "user.player.player-name-length",
                new Dictionary<string, object?>
                {
                    ["min"] = option.GetInt("player_name_length_min", 3).ToString(),
                    ["max"] = option.GetInt("player_name_length_max", 16).ToString(),
                }))
            .With("score", user.Score)
            .With("cost", option.GetInt("score_per_player", 100));

        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), host, context.Request.Path.Value ?? "/user/player",
            settings.Debug, "user", session);
        vars["pageTitle"] = Trans(context, "general.player-manage");
        vars["errors"] = TemplateErrors.Empty;
        vars["gridRows"] = PageView.Grid(new List<object?>
        {
            PageView.Column("md-6", "user/widgets/players/list",
                "user/widgets/players/notice"),
            PageView.Column("md-6", "shared/previewer"),
        });
        vars["extra_json"] = PhpJson.Encode(extra, 0);
        string html = TemplateRenderer.Render("user/player", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };
    // ================================================================== 辅助

    private async Task<(UserRepository.UserRow?, PlayerRow?)> OwnedPlayerAsync(
        HttpContext context, IReadOnlyDictionary<string, string> parameters)
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return (null, null);
        }

        int pid = parameters.TryGetValue("player", out string? raw) ? RequestInput.ToInt(raw) : 0;
        PlayerRow? player = players.FindById(pid);
        if (player is null)
        {
            context.Response.StatusCode = 404;
            return (null, null);
        }

        if (player.Uid != user.Uid)
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "admin.players.no-permission")));
            return (null, null);
        }

        return (user, player);
    }

    private string Timezone => "Asia/Shanghai";

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

    /// <summary><c>Player::toArray()</c> 的键序。</summary>
    private static Dictionary<string, object?> PlayerJson(int pid, int uid, string name,
        int tidCape, string? lastModified, int tidSkin) =>
        new(StringComparer.Ordinal)
        {
            ["pid"] = pid,
            ["uid"] = uid,
            ["name"] = name,
            ["tid_cape"] = tidCape,
            ["last_modified"] = lastModified,
            ["tid_skin"] = tidSkin,
        };

    private static Dictionary<string, object?> Message(int code, string message) =>
        new(StringComparer.Ordinal) { ["code"] = code, ["message"] = message };

    private static Dictionary<string, object?> Message(int code, string message,
        Dictionary<string, object?> data) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["message"] = message,
            ["data"] = data,
        };

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
