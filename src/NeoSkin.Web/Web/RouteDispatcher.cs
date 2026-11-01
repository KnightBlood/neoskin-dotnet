using NeoSkin.Web.Support;

namespace NeoSkin.Web.Web;

/// <summary>已移植的控制器 handler。</summary>
public delegate Task RequestHandler(HttpContext context, RouteDefinition route,
    IReadOnlyDictionary<string, string> parameters);

/// <summary>action → handler（未移植的返回 null，由分发器回 501）。</summary>
public interface IHandlerResolver
{
    RequestHandler? Resolve(string action, string uri);
}

/// <summary>Phase 3 骨架：所有 action 都还没接线，一律 501。</summary>
public sealed class NotImplementedHandlerResolver : IHandlerResolver
{
    public RequestHandler? Resolve(string action, string uri) => null;
}

/// <summary>
/// 路由分发器：ASP.NET Core 的终端处理器，按 <see cref="RouteTable"/> 匹配 174 条路由。
///
/// <p>与 Laravel 对齐的三条语义：</p>
/// <list type="number">
///   <item>路径不命中 → 404（消息 <c>The route X could not be found.</c>）；</item>
///   <item>路径命中、方法不对 → 405，<c>Allow: GET, HEAD, POST</c>（逗号后带空格）；</item>
///   <item>路径命中、<c>OPTIONS</c> → 200 空体，<c>Allow: GET,HEAD,POST</c>（逗号<b>不带</b>空格）。</item>
/// </list>
/// </summary>
public sealed class RouteDispatcher(
    Routes routes,
    IHandlerResolver resolver,
    Middleware.MiddlewarePipeline? pipeline = null,
    Service.Option? option = null,
    Config.NeoSkinSettings? settings = null)
{
    private readonly Error.ErrorPages? _errorPages =
        option is null || settings is null ? null : new Error.ErrorPages(option, settings);
    private const string NotImplementedBody = "{\"code\":501,\"message\":\"该路由尚未移植\"}";

    private const string DefaultCacheControl = "no-cache, private";

    private readonly RouteTable _table = new(routes);

    public RouteTable Table => _table;

    public async Task DispatchAsync(HttpContext context)
    {
        string rawPath = context.Request.Path.Value ?? "/";

        // 双斜杠守卫：源站对 /a//b 返回 404（先剥掉尾斜杠再判断）
        if (RouteTable.HasInnerDoubleSlash(rawPath))
        {
            await WriteErrorAsync(context, 404,
                "The route " + RouteTable.LaravelPath(rawPath) + " could not be found.");
            return;
        }

        string path = RouteTable.LaravelPath(rawPath);
        RouteMatch? match = _table.Match(context.Request.Method, path);

        if (match is not null)
        {
            IReadOnlyList<string> methods = match.Route.Methods;
            context.Items["route.name"] = match.Route.Name;
            context.Items["route.middleware"] = match.Route.Middleware;

            async Task Terminal()
            {
                RequestHandler? handler = resolver.Resolve(match.Route.Action, match.Route.Uri);
                if (handler is null)
                {
                    context.Response.StatusCode = 501;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(NotImplementedBody);
                    return;
                }

                await handler(context, match.Route, match.Parameters);
            }

            // 中间件必须对「未移植的 action」也生效（源站的行为与控制器无关），
            // 所以 501 兜底放在中间件链的末端。
            if (pipeline is null)
            {
                await Terminal();
            }
            else
            {
                await pipeline.RunAsync(context, match.Route, Terminal);
            }

            return;
        }

        IReadOnlyList<string> allowed = _table.AllowedMethods(path);
        if (allowed.Count == 0)
        {
            await WriteErrorAsync(context, 404, "The route " + path + " could not be found.");
            return;
        }

        if (string.Equals(context.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 200;
            context.Response.Headers["Allow"] = string.Join(",", allowed);
            context.Response.Headers.CacheControl = DefaultCacheControl;
            context.Response.ContentType = "text/html; charset=utf-8";
            return;
        }

        context.Response.Headers["Allow"] = string.Join(", ", allowed);
        await WriteErrorAsync(context, 405,
            "The " + context.Request.Method.ToUpperInvariant() + " method is not supported for route "
            + path + ". Supported methods: " + string.Join(", ", allowed) + ".");
    }

    /// <summary>
    /// 错误响应：<c>expectsJson()</c> 为真时输出 Laravel 的 pretty JSON
    /// （<c>{message, exception:true, trace:[]}</c>），否则输出 HTML。
    ///
    /// <p>HTML 分支在模板引擎接入前先用一个最小页面占位（标题与 Symfony 的
    /// 错误页一致），见 PORT-NOTES 的待办。</p>
    /// </summary>
    private async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = DefaultCacheControl;

        if (Error.ContentNegotiation.ExpectsJson(context))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(Error.ErrorPayload.Json(message));
            return;
        }

        if (_errorPages is not null)
        {
            await _errorPages.RenderAsync(context, status, message);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        string title = status + " " + Error.HttpStatus.Text(status);
        await context.Response.WriteAsync(
            "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>" + title
            + "</title></head><body><h1>" + title + "</h1><p>"
            + System.Net.WebUtility.HtmlEncode(message) + "</p></body></html>");
    }
}
