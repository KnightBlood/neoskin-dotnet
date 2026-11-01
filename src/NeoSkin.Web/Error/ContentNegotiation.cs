namespace NeoSkin.Web.Error;

/// <summary>
/// Laravel <c>$request->expectsJson()</c> 的等价实现。
///
/// <p>定义：<c>($ajax &amp;&amp; !$pjax &amp;&amp; $this->acceptsAnyContentType()) || $this->wantsJson()</c>，
/// 三个条件都不能省：</p>
/// <list type="bullet">
///   <item><c>wantsJson()</c> 看 <c>Accept</c> <b>排序后第一项</b>是否含 <c>/json</c>
///     或 <c>+json</c> —— 所以 <c>Accept: text/html,application/json</c> 走 HTML；</item>
///   <item><c>acceptsAnyContentType()</c> 要求 Accept 为空或含 <c>*/*</c>；</item>
///   <item><c>ajax</c> = <c>X-Requested-With: XMLHttpRequest</c>，<c>pjax</c> = <c>X-PJAX</c>。</item>
/// </list>
/// </summary>
public static class ContentNegotiation
{
    /// <summary><c>expectsJson()</c>。</summary>
    public static bool ExpectsJson(HttpContext context)
    {
        string accept = context.Request.Headers.Accept.ToString();
        bool ajax = string.Equals(context.Request.Headers["X-Requested-With"].ToString(),
            "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        bool pjax = context.Request.Headers.ContainsKey("X-PJAX");
        bool acceptsAny = accept.Length == 0 || accept.Contains("*/*", StringComparison.Ordinal);
        bool wantsJson = FirstMediaType(accept) is { } media
                         && (media.Contains("/json", StringComparison.OrdinalIgnoreCase)
                             || media.Contains("+json", StringComparison.OrdinalIgnoreCase));

        return (ajax && !pjax && acceptsAny) || wantsJson;
    }

    /// <summary>Accept 头的第一个媒体类型（去掉 <c>;q=</c> 等参数）。</summary>
    private static string? FirstMediaType(string accept)
    {
        foreach (string part in accept.Split(','))
        {
            string media = part.Split(';')[0].Trim();
            if (media.Length > 0)
            {
                return media;
            }
        }

        return null;
    }
}
