using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web.Pagination;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\UsersManagementController</c> 的移植（web 与 api 共用）。
/// </summary>
public sealed class UsersManagementController(
    Option option,
    UserRepository users,
    ICipher cipher)
{
    private const int PerPage = 10;

    /// <summary><c>GET admin/users/list</c> / <c>GET api/admin/users</c>。</summary>
    public RequestHandler List() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string q = RequestInput.String(input, "q");
        int page = Math.Max(1, RequestInput.Int(input, "page"));

        SearchString.Result compiled = SearchString.Compile(q, UserSearchOptions());
        UserRepository.AdminPageResult result = users.AdminPage(compiled, page, PerPage);

        string path = "http://" + Host(context) + (context.Request.Path.Value ?? "/");
        Dictionary<string, object?> body = Paginator.Body(path, page, PerPage, result.Total);
        body["data"] = result.Rows;
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>PUT admin/users/{user}/email</c>。</summary>
    public RequestHandler Email() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string email = RequestInput.String(input, "email");
        if (!await ValidateAsync(context, input,
                ("email", ["required", "email",
                    $"unique:users,email,{target.Uid},uid"])))
        {
            return;
        }

        users.UpdateEmail(target.Uid, email);
        await JsonAsync(context, 200,
            Message(0, Trans(context, "admin.users.operations.email.success")));
    };

    /// <summary><c>PUT admin/users/{user}/verification</c>：切换 verified。</summary>
    public RequestHandler Verification() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        users.UpdateVerified(target.Uid, !target.Verified);
        await JsonAsync(context, 200,
            Message(0, Trans(context, "admin.users.operations.verification.success")));
    };

    /// <summary><c>PUT admin/users/{user}/nickname</c>。</summary>
    public RequestHandler Nickname() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        if (!await ValidateAsync(context, input, ("nickname", ["required", "string"])))
        {
            return;
        }

        string nickname = RequestInput.String(input, "nickname");
        users.UpdateNickname(target.Uid, nickname);
        await JsonAsync(context, 200, Message(0,
            Trans(context, "admin.users.operations.nickname.success",
                new Dictionary<string, object?> { ["new"] = nickname })));
    };

    /// <summary><c>PUT admin/users/{user}/password</c>。</summary>
    public RequestHandler Password() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        if (!await ValidateAsync(context, input,
                ("password", ["required", "string", "min:8", "max:16"])))
        {
            return;
        }

        string password = RequestInput.String(input, "password");
        users.ChangePassword(target.Uid, cipher.Hash(password, option.GetString("pwd_salt", null)));
        await JsonAsync(context, 200,
            Message(0, Trans(context, "admin.users.operations.password.success")));
    };

    /// <summary><c>PUT admin/users/{user}/score</c>。</summary>
    public RequestHandler Score() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        if (!await ValidateAsync(context, input, ("score", ["required", "integer"])))
        {
            return;
        }

        users.UpdateScore(target.Uid, RequestInput.Int(input, "score"));
        await JsonAsync(context, 200,
            Message(0, Trans(context, "admin.users.operations.score.success")));
    };

    /// <summary><c>PUT admin/users/{user}/permission</c>：超级管理员才能授予 admin；不能改自己。</summary>
    public RequestHandler Permission() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        if (!await ValidateAsync(context, input, ("permission", ["required", "in:-1,0,1"])))
        {
            return;
        }

        UserRepository.UserRow? auth = CurrentUser(context);
        int permission = RequestInput.Int(input, "permission");
        if (auth is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if ((permission == UserRepository.Admin && auth.Permission < UserRepository.SuperAdmin)
            || target.Uid == auth.Uid)
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "admin.users.operations.no-permission")));
            return;
        }

        users.UpdatePermission(target.Uid, permission);
        await JsonAsync(context, 200,
            Message(0, Trans(context, "admin.users.operations.permission")));
    };

    /// <summary><c>DELETE admin/users/{user}</c>：先删角色再删用户。</summary>
    public RequestHandler Delete() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? target = await GuardAsync(context, parameters);
        if (target is null)
        {
            return;
        }

        users.DeleteWithPlayers(target.Uid);
        await JsonAsync(context, 200,
            Message(0, Trans(context, "admin.users.operations.delete.success")));
    };

    // ================================================================== 辅助

    private static SearchString.Options UserSearchOptions() =>
        new SearchString.Options()
            .Column("uid")
            .Searchable("email")
            .Searchable("nickname")
            .Column("avatar")
            .Column("score")
            .Column("permission")
            .Column("ip")
            .Date("last_sign_at")
            .Date("register_at")
            .BooleanColumn("verified")
            .BooleanColumn("is_dark_mode")
            .Keyword("sort", "order_by")
            .Keyword("fields", "select")
            .Keyword("limit", "limit")
            .Keyword("from", "offset");

    /// <summary>隐式绑定 + 权限闭包：目标不是自己且权限 ≥ 自己 → 403。</summary>
    private async Task<UserRepository.UserRow?> GuardAsync(HttpContext context,
        IReadOnlyDictionary<string, string> parameters)
    {
        int uid = parameters.TryGetValue("user", out string? raw)
            ? RequestInput.ToInt(raw)
            : 0;
        UserRepository.UserRow? target = users.FindById(uid);
        if (target is null)
        {
            context.Response.StatusCode = 404;
            return null;
        }

        UserRepository.UserRow? auth = CurrentUser(context);
        if (auth is not null && target.Uid != auth.Uid && target.Permission >= auth.Permission)
        {
            await JsonAsync(context, 403,
                Message(1, Trans(context, "admin.users.operations.no-permission")));
            return null;
        }

        return target;
    }

    private async Task<bool> ValidateAsync(HttpContext context,
        Dictionary<string, object?> input, params (string Field, string[] Rules)[] rules)
    {
        var v = new Validator(input, Translator(context), Locale(context), option,
            new DbPresenceVerifier(users.Database));
        foreach ((string field, string[] fieldRules) in rules)
        {
            foreach (string rule in fieldRules)
            {
                v.AddRule(field, rule);
            }
        }

        if (v.Passes())
        {
            return true;
        }

        await ValidationFailedAsync(context, v.Errors.ToDictionary(
            pair => pair.Key, pair => pair.Value));
        return false;
    }

    private static UserRepository.UserRow? CurrentUser(HttpContext context) =>
        AuthMiddleware.CurrentUser(context);

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Host(HttpContext context) =>
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
