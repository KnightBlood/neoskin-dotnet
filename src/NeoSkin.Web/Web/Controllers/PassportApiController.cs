using System.Security.Cryptography;
using NeoSkin.Web.Db;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Service.OAuth;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// Passport 的 JSON API：OAuth 客户端、访问令牌、个人访问令牌、作用域。
/// </summary>
public sealed class PassportApiController(
    OAuthService oauth,
    I18n.Translator? translator,
    ICipher cipher)
{
    // ============================================================ clients

    /// <summary><c>ClientController@forUser</c>。</summary>
    public RequestHandler ClientsForUser() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var list = new List<Dictionary<string, object?>>();
        foreach (OAuthStore.Client client in oauth.Store.ClientsForUser(user.Uid))
        {
            list.Add(OAuthStore.ClientJson(client));
        }

        await JsonAsync(context, 200, list);
    };

    /// <summary><c>ClientController@store</c>。</summary>
    public RequestHandler StoreClient() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        Dictionary<string, object?> input = await InputAsync(context);
        Dictionary<string, List<string>> errors = ValidateClient(input);
        if (errors.Count > 0)
        {
            await ValidationErrorAsync(context, errors);
            return;
        }

        string redirect = Str(input, "redirect");
        bool confidential = !(input.TryGetValue("confidential", out object? raw)
                              && IsFalse(raw));
        string? plainSecret = confidential ? RandomAlnum(40) : null;
        long id = oauth.Store.CreateClient(user?.Uid, Str(input, "name"),
            plainSecret is null ? null : cipher.Hash(plainSecret, ""), null,
            string.Join(",", SplitRedirects(redirect)), false, false);

        OAuthStore.Client? created = oauth.Store.FindClient(id);
        if (created is null)
        {
            context.Response.StatusCode = 500;
            return;
        }

        Dictionary<string, object?> body = OAuthStore.ClientJson(created);
        if (confidential && plainSecret is not null)
        {
            body["plain_secret"] = plainSecret;
        }

        body.Remove("secret");
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>ClientController@update</c>。</summary>
    public RequestHandler UpdateClient() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long id = ToLong(parameters, "client_id");
        OAuthStore.Client? client = oauth.Store.FindClientForUser(id, user.Uid);
        if (client is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        Dictionary<string, object?> input = await InputAsync(context);
        Dictionary<string, List<string>> errors = ValidateClient(input);
        if (errors.Count > 0)
        {
            await ValidationErrorAsync(context, errors);
            return;
        }

        oauth.Store.UpdateClient(id, Str(input, "name"),
            string.Join(",", SplitRedirects(Str(input, "redirect"))));

        OAuthStore.Client? updated = oauth.Store.FindClient(id);
        if (updated is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        Dictionary<string, object?> body = OAuthStore.ClientJson(updated);
        body.Remove("secret");
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>ClientController@destroy</c>。</summary>
    public RequestHandler DestroyClient() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long id = ToLong(parameters, "client_id");
        OAuthStore.Client? client = oauth.Store.FindClientForUser(id, user.Uid);
        if (client is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        oauth.Store.DeleteClient(id);
        context.Response.StatusCode = 204;
        await Task.CompletedTask;
    };

    private Dictionary<string, List<string>> ValidateClient(Dictionary<string, object?> input)
    {
        var validator = new Validator(input, translator ?? FallbackTranslator(),
            translator?.GetLocale() ?? "en", null, null);
        validator.AddRule("name", "required");
        validator.AddRule("name", "max:255");
        validator.AddRule("redirect", "required");
        return validator.Passes()
            ? []
            : validator.Errors.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static List<string> SplitRedirects(string redirect) =>
        redirect.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

    // ============================================================ tokens

    /// <summary><c>AuthorizedAccessTokenController@forUser</c>。</summary>
    public RequestHandler TokensForUser() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var list = new List<Dictionary<string, object?>>();
        foreach (OAuthStore.Token token in oauth.Store.ActiveTokensForUser(user.Uid))
        {
            OAuthStore.Client? client = oauth.Store.FindActiveClient(token.ClientId);
            if (client is null || client.UserId is null)
            {
                // first-party clients 不出现在「授权应用」列表
                continue;
            }

            list.Add(TokenJson(token, client));
        }

        await JsonAsync(context, 200, list);
    };

    /// <summary><c>AuthorizedAccessTokenController@destroy</c>。</summary>
    public RequestHandler DestroyToken() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string id = parameters.TryGetValue("token_id", out string? raw) ? raw ?? "" : "";
        OAuthStore.Token? token = oauth.Store.FindTokenForUser(id, user.Uid);
        if (token is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        oauth.Store.RevokeToken(id);
        OAuthStore.RefreshToken? refresh = oauth.Store.RefreshTokenForAccessToken(id);
        if (refresh is not null)
        {
            oauth.Store.RevokeRefreshToken(refresh.Id);
        }

        context.Response.StatusCode = 204;
        await Task.CompletedTask;
    };

    // ============================================================ personal

    /// <summary><c>PersonalAccessTokenController@forUser</c>。</summary>
    public RequestHandler PersonalTokensForUser() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var list = new List<Dictionary<string, object?>>();
        foreach (OAuthStore.Token token in oauth.Store.ActiveTokensForUser(user.Uid))
        {
            OAuthStore.Client? client = oauth.Store.FindActiveClient(token.ClientId);
            if (client is null || !client.PersonalAccessClient)
            {
                continue;
            }

            list.Add(TokenJson(token, client));
        }

        await JsonAsync(context, 200, list);
    };

    /// <summary><c>PersonalAccessTokenController@store</c>。</summary>
    public RequestHandler StorePersonalToken() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await InputAsync(context);
        var validator = new Validator(input, translator ?? FallbackTranslator(),
            translator?.GetLocale() ?? "en", null, null);
        validator.AddRule("name", "required");
        validator.AddRule("name", "max:255");
        if (!validator.Passes())
        {
            await ValidationErrorAsync(context, validator.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        var scopes = new List<string>();
        foreach (string scope in OAuthService.StringList(input.GetValueOrDefault("scopes")))
        {
            if (!oauth.HasScope(scope))
            {
                await ValidationErrorAsync(context, new Dictionary<string, List<string>>
                {
                    ["scopes"] = ["The selected scopes is invalid."],
                });
                return;
            }

            scopes.Add(scope);
        }

        OAuthStore.Client? personal = oauth.Store.PersonalAccessClient();
        if (personal is null)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(
                "Personal access client not found for 'users' user provider. Please create one.");
            return;
        }

        OAuthService.Issued issued = oauth.Issue(user.Uid, personal.Id, Str(input, "name"),
            scopes, false);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accessTokenId"] = issued.TokenId,
            ["tokenType"] = "Bearer",
            ["expiresIn"] = issued.ExpiresIn,
            ["accessToken"] = issued.AccessToken,
        };
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>PersonalAccessTokenController@destroy</c>。</summary>
    public RequestHandler DestroyPersonalToken() => DestroyToken();

    // ============================================================ scopes

    /// <summary><c>ScopeController@all</c>。</summary>
    public RequestHandler Scopes() => async (context, route, parameters) =>
    {
        await JsonAsync(context, 200, oauth.ScopeJson());
    };

    // ============================================================ helpers

    private static Dictionary<string, object?> TokenJson(OAuthStore.Token token,
        OAuthStore.Client client) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = token.Id,
            ["user_id"] = token.UserId,
            ["client_id"] = token.ClientId,
            ["name"] = token.Name,
            ["scopes"] = token.Scopes,
            ["revoked"] = token.Revoked,
            ["created_at"] = token.CreatedAt,
            ["updated_at"] = token.UpdatedAt,
            ["expires_at"] = token.ExpiresAt,
            ["client"] = OAuthStore.ClientJson(client),
        };

    private static bool IsFalse(object? value) => value switch
    {
        null => false,
        bool b => !b,
        string s => s is "0" or "false" or "",
        long l => l == 0,
        int i => i == 0,
        _ => false,
    };

    private static string RandomAlnum(int length)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new System.Text.StringBuilder(length);
        foreach (byte b in RandomNumberGenerator.GetBytes(length))
        {
            sb.Append(Alphabet[b % Alphabet.Length]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 读取请求参数。源由 <c>App\Http\Middleware\RequestInput</c> 统一处理
    /// （query + form / JSON），前端 <c>blessing.fetch</c> 的写请求一律发 JSON。
    /// </summary>
    private static Task<Dictionary<string, object?>> InputAsync(HttpContext context) =>
        RequestInput.BagAsync(context);

    private static string Str(Dictionary<string, object?> input, string key) =>
        input.TryGetValue(key, out object? value) && value is not null
            ? PhpString.Cast(value)
            : "";

    private static long ToLong(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out string? value) && long.TryParse(value, out long parsed)
            ? parsed
            : 0;

    private static I18n.Translator FallbackTranslator() =>
        new(new I18n.TranslationFileLoader(), "en", "en");

    private async Task ValidationErrorAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = Validator.Summarize(errors),
            ["errors"] = errors,
        };
        await JsonAsync(context, 422, body);
    }

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
