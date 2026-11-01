using NeoSkin.Web.Db;
using NeoSkin.Web.Error;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Service.OAuth;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// Laravel Passport 的 OAuth 端点（<c>/oauth/token</c>、<c>/oauth/authorize</c>、
/// <c>/oauth/token/refresh</c>）。
///
/// <p>与 Java 版 <c>PassportOAuthController</c> 同构。授权页 HTML 暂用最小可用的
/// 表单（Phase 7 模板引擎接入后替换为 <c>oauth/authorize</c> 模板）。</p>
/// </summary>
public sealed class PassportOAuthController(
    OAuthService oauth,
    I18n.Translator? translator,
    ICipher cipher)
{
    private static readonly string[] SessionKeys =
    [
        "oauth_client_id", "oauth_redirect_uri", "oauth_state", "oauth_scopes",
        "oauth_auth_token",
    ];

    // ============================================================ token

    /// <summary><c>POST /oauth/token</c>。</summary>
    public RequestHandler IssueToken() => async (context, route, parameters) =>
    {
        try
        {
            Dictionary<string, object?> input = await InputAsync(context);
            string grant = Str(input, "grant_type");
            Dictionary<string, object?> response = grant switch
            {
                "authorization_code" => await Task.FromResult(AuthorizationCodeGrant(input)),
                "refresh_token" => await Task.FromResult(RefreshTokenGrant(input)),
                "client_credentials" => await Task.FromResult(ClientCredentialsGrant(input)),
                _ => throw new OAuthError("unsupported_grant_type",
                    "The authorization grant type is not supported by the authorization server.",
                    400),
            };

            await JsonAsync(context, 200, response);
        }
        catch (OAuthError e)
        {
            await JsonAsync(context, e.Status, ErrorBody(e));
        }
        catch (Exception)
        {
            await JsonAsync(context, 500, ErrorBody(new OAuthError("server_error",
                "The authorization server encountered an unexpected condition.", 500)));
        }
    };

    private Dictionary<string, object?> AuthorizationCodeGrant(Dictionary<string, object?> input)
    {
        OAuthStore.Client client = RequireClient(input);
        string code = Str(input, "code");
        if (code.Length == 0)
        {
            throw new OAuthError("invalid_request",
                "The request is missing a required parameter.", 400);
        }

        OAuthStore.AuthCode? authCode = oauth.Store.FindAuthCode(code);
        if (authCode is null || authCode.Revoked)
        {
            throw new OAuthError("invalid_grant", "Authorization code has been revoked", 400);
        }

        if (authCode.ExpiresAt is not null
            && string.CompareOrdinal(authCode.ExpiresAt, PhpDate.Now(Timezone)) < 0)
        {
            throw new OAuthError("invalid_grant", "Authorization code has expired", 400);
        }

        if (authCode.ClientId != client.Id)
        {
            throw new OAuthError("invalid_request",
                "Authorization code was not issued to this client", 400);
        }

        oauth.Store.RevokeAuthCode(code);
        OAuthService.Issued issued = oauth.Issue(authCode.UserId, client.Id, null,
            authCode.Scopes, true);
        return TokenResponse(issued);
    }

    private Dictionary<string, object?> RefreshTokenGrant(Dictionary<string, object?> input)
    {
        string refresh = Str(input, "refresh_token");
        OAuthStore.RefreshToken? token = refresh.Length == 0
            ? null
            : oauth.Store.FindRefreshToken(refresh);
        if (token is null || token.Revoked)
        {
            throw new OAuthError("invalid_grant", "The refresh token is invalid.", 400);
        }

        if (token.ExpiresAt is not null
            && string.CompareOrdinal(token.ExpiresAt, PhpDate.Now(Timezone)) < 0)
        {
            throw new OAuthError("invalid_grant", "The refresh token is expired.", 400);
        }

        OAuthStore.Token? access = oauth.Store.FindToken(token.AccessTokenId);
        if (access is null)
        {
            throw new OAuthError("invalid_grant", "The refresh token is invalid.", 400);
        }

        OAuthStore.Client? client = oauth.Store.FindActiveClient(access.ClientId);
        if (client is null)
        {
            throw new OAuthError("invalid_client", "Client authentication failed", 401);
        }

        string clientId = Str(input, "client_id");
        if (clientId.Length > 0 && (!long.TryParse(clientId, out long given) || given != client.Id))
        {
            throw new OAuthError("invalid_grant",
                "The refresh token is not valid for this client.", 400);
        }

        oauth.Store.RevokeRefreshToken(refresh);
        oauth.Store.RevokeToken(access.Id);
        OAuthService.Issued issued = oauth.Issue(access.UserId, client.Id, access.Name,
            access.Scopes, true);
        return TokenResponse(issued);
    }

    private Dictionary<string, object?> ClientCredentialsGrant(Dictionary<string, object?> input)
    {
        OAuthStore.Client client = RequireClient(input);
        if (client.UserId is not null || string.IsNullOrEmpty(client.Secret))
        {
            throw new OAuthError("invalid_client", "Client authentication failed", 401);
        }

        List<string> scopes = RequestedScopes(input);
        OAuthService.Issued issued = oauth.Issue(null, client.Id, null, scopes, false);
        return TokenResponse(issued);
    }

    private OAuthStore.Client RequireClient(Dictionary<string, object?> input)
    {
        string clientId = Str(input, "client_id");
        if (clientId.Length == 0)
        {
            throw new OAuthError("invalid_request",
                "The request is missing a required parameter: client_id", 400);
        }

        OAuthStore.Client? client = long.TryParse(clientId, out long id)
            ? oauth.Store.FindActiveClient(id)
            : null;
        if (client is null)
        {
            throw new OAuthError("invalid_client", "Client authentication failed", 401);
        }

        string secret = Str(input, "client_secret");
        if (!string.IsNullOrEmpty(client.Secret)
            && !oauth.Store.VerifyClientSecret(client, secret, cipher))
        {
            throw new OAuthError("invalid_client", "Client authentication failed", 401);
        }

        return client;
    }

    private List<string> RequestedScopes(Dictionary<string, object?> input)
    {
        var scopes = new List<string>();
        string raw = Str(input, "scope");
        if (raw.Length == 0)
        {
            return scopes;
        }

        foreach (string part in raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!oauth.HasScope(part))
            {
                throw new OAuthError("invalid_scope",
                    "The requested scope is invalid, unknown, or malformed.", 400);
            }

            scopes.Add(part);
        }

        return scopes;
    }

    private static Dictionary<string, object?> TokenResponse(OAuthService.Issued issued)
    {
        var response = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["token_type"] = "Bearer",
            ["expires_in"] = issued.ExpiresIn,
            ["access_token"] = issued.AccessToken,
        };
        if (issued.RefreshToken is not null)
        {
            response["refresh_token"] = issued.RefreshToken;
        }

        return response;
    }

    // ============================================================ authorize

    /// <summary><c>GET /oauth/authorize</c>。</summary>
    public RequestHandler Authorize() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string clientId = context.Request.Query["client_id"].ToString() ?? "";
        string redirectUri = context.Request.Query["redirect_uri"].ToString() ?? "";
        string responseType = context.Request.Query["response_type"].ToString() ?? "";
        string state = context.Request.Query["state"].ToString() ?? "";
        string scopeRaw = context.Request.Query["scope"].ToString() ?? "";

        OAuthStore.Client? client = long.TryParse(clientId, out long id)
            ? oauth.Store.FindActiveClient(id)
            : null;
        if (client is null)
        {
            await JsonAsync(context, 400, ErrorBody(new OAuthError("invalid_client",
                "Client authentication failed", 400)));
            return;
        }

        if (responseType.Length > 0 && responseType != "code")
        {
            await JsonAsync(context, 400, ErrorBody(new OAuthError("unsupported_response_type",
                "The authorization server does not support obtaining an access token using this method.",
                400)));
            return;
        }

        if (redirectUri.Length > 0 && !client.RedirectUris().Contains(redirectUri))
        {
            await JsonAsync(context, 400, ErrorBody(new OAuthError("invalid_request",
                "Invalid redirect URI", 400)));
            return;
        }

        if (redirectUri.Length == 0)
        {
            List<string> uris = client.RedirectUris();
            redirectUri = uris.Count == 0 ? "" : uris[0];
        }

        var scopes = new List<string>();
        if (scopeRaw.Length > 0)
        {
            scopes.AddRange(scopeRaw.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            scopes.Add("User.Read");
        }

        // 已授权过相同 scope → 直接签发 code（源 hasGrantedScopes）
        if (oauth.Store.HasScopesForClientAndUser(user.Uid, client.Id, scopes))
        {
            IssueCodeAndRedirect(context, client, user, scopes, redirectUri, state);
            return;
        }

        SessionStore? session = Session(context);
        string authToken = OAuthService.RandomHex(20);
        if (session is not null)
        {
            session.Put("oauth_client_id", (long)client.Id);
            session.Put("oauth_redirect_uri", redirectUri);
            session.Put("oauth_state", state);
            session.Put("oauth_scopes", scopes);
            session.Put("oauth_auth_token", authToken);
        }

        // oauth/authorize 模板（源模板的 DOM 契约）
        var scopeRows = new List<object?>();
        foreach (string scope in scopes)
        {
            string key = oauth.Scopes.TryGetValue(scope, out string? description)
                ? description
                : scope;
            string text = translator?.Trans(key) ?? key;
            scopeRows.Add(new PhpArray().With("id", scope).With("description", text));
        }

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["locale"] = translator?.GetLocale() ?? "zh_CN",
            ["client"] = new PhpArray().With("name", client.Name),
            ["user"] = user.Nickname ?? user.Email,
            ["scopes"] = scopeRows,
            ["approve_url"] = "/oauth/authorize",
            ["auth_token"] = authToken,
        };

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            View.TemplateRenderer.Render("oauth/authorize", vars));
    };

    /// <summary><c>POST /oauth/authorize</c>：同意授权。</summary>
    public RequestHandler Approve() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        if (session is null)
        {
            context.Response.StatusCode = 403;
            return;
        }

        Dictionary<string, object?> input = await InputAsync(context);
        string posted = Str(input, "auth_token");
        string stored = session.Get("oauth_auth_token")?.ToString() ?? "";
        if (stored.Length == 0 || !string.Equals(stored, posted, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            return;
        }

        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        OAuthStore.Client? client = long.TryParse(session.Get("oauth_client_id")?.ToString(),
            out long clientId)
            ? oauth.Store.FindActiveClient(clientId)
            : null;
        if (user is null || client is null)
        {
            context.Response.StatusCode = 403;
            return;
        }

        List<string> scopes = OAuthService.StringList(session.Get("oauth_scopes"));
        if (scopes.Count == 0)
        {
            scopes = ["User.Read"];
        }

        string redirectUri = session.Get("oauth_redirect_uri")?.ToString() ?? "";
        string state = session.Get("oauth_state")?.ToString() ?? "";
        foreach (string key in SessionKeys)
        {
            session.Forget(key);
        }

        IssueCodeAndRedirect(context, client, user, scopes, redirectUri, state);
    };

    /// <summary><c>DELETE /oauth/authorize</c>：拒绝授权。</summary>
    public RequestHandler Deny() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        string redirectUri = session?.Get("oauth_redirect_uri")?.ToString() ?? "";
        string state = session?.Get("oauth_state")?.ToString() ?? "";
        if (session is not null)
        {
            foreach (string key in SessionKeys)
            {
                session.Forget(key);
            }
        }

        if (string.IsNullOrEmpty(redirectUri))
        {
            context.Response.StatusCode = 400;
            return;
        }

        string target = redirectUri + (redirectUri.Contains('?') ? "&" : "?") + "error=access_denied";
        if (state.Length > 0)
        {
            target += "&state=" + Uri.EscapeDataString(state);
        }

        Redirect(context, target);
        await Task.CompletedTask;
    };

    private void IssueCodeAndRedirect(HttpContext context, OAuthStore.Client client,
        UserRepository.UserRow user, List<string> scopes, string redirectUri, string state)
    {
        string code = OAuthService.RandomHex(40);
        oauth.Store.InsertAuthCode(code, user.Uid, client.Id, scopes,
            PhpDate.At((DateTimeOffset.UtcNow.ToUnixTimeSeconds() + OAuthService.CodeTtlSeconds)
                * 1000, Timezone));

        string target = redirectUri + (redirectUri.Contains('?') ? "&" : "?") + "code=" + code;
        if (state.Length > 0)
        {
            target += "&state=" + Uri.EscapeDataString(state);
        }

        Redirect(context, target);
    }

    // ============================================================ refresh

    /// <summary><c>POST /oauth/token/refresh</c>：写 transient cookie。</summary>
    public RequestHandler TransientRefresh() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        SessionStore? session = Session(context);
        string csrf = session?.Token()?.ToString() ?? "";
        (string value, int maxAge) = oauth.TransientCookie(user.Uid, csrf, 120);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=UTF-8";
        if (value.Length > 0)
        {
            context.Response.Headers.Append("Set-Cookie",
                OAuthService.CookieName + "=" + value
                + "; Path=/; Max-Age=" + maxAge
                + "; HttpOnly; SameSite=Lax");
        }

        await context.Response.WriteAsync("Refreshed.");
    };

    // ============================================================ helpers

    private string Timezone => "Asia/Shanghai";

    private IEnumerable<string> ScopeRows(IEnumerable<string> scopes)
    {
        IReadOnlyDictionary<string, string> all = oauth.Scopes;
        foreach (string scope in scopes)
        {
            string key = all.TryGetValue(scope, out string? description) ? description : scope;
            string text = translator?.Trans(key) as string ?? key;
            yield return "<li><code>" + System.Net.WebUtility.HtmlEncode(scope)
                + "</code> — " + System.Net.WebUtility.HtmlEncode(text) + "</li>";
        }
    }

    /// <summary>
    /// 读取请求参数（query + form / JSON，语义同 <c>App\Http\Middleware\RequestInput</c>）。
    /// </summary>
    private static Task<Dictionary<string, object?>> InputAsync(HttpContext context) =>
        RequestInput.BagAsync(context);

    private static string Str(Dictionary<string, object?> input, string key) =>
        input.TryGetValue(key, out object? value) && value is not null
            ? PhpString.Cast(value)
            : "";

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-cache, private";
    }

    private static Dictionary<string, object?> ErrorBody(OAuthError error) =>
        new(StringComparer.Ordinal)
        {
            ["error"] = error.Error,
            ["error_description"] = error.Description,
        };

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
}

/// <summary>OAuth 错误（源 <c>League\OAuth2\Server\Exception\OAuthServerException</c> 子集）。</summary>
public sealed class OAuthError(string error, string description, int status)
    : Exception(description)
{
    public string Error { get; } = error;

    public string Description { get; } = description;

    public int Status { get; } = status;
}
