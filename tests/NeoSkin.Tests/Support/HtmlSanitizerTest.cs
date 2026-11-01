using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>公告/站内信 HTML 消毒（Java <c>CommunicationRenderer.sanitize</c> 的移植）。</summary>
public sealed class HtmlSanitizerTest
{
    [Fact]
    public void RemovesDangerousElementsAndAttributes()
    {
        string html = HtmlSanitizer.Sanitize(
            "<p onclick=\"evil()\">Hi<script>alert(1)</script></p>"
            + "<iframe src=\"https://x\"></iframe><img src=\"javascript:x\" onerror=\"y\">",
            "p");

        Assert.DoesNotContain("script", html);
        Assert.DoesNotContain("iframe", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("<p>Hi</p>", html);
        Assert.Contains("<img class=\"img-fluid\">", html);
    }

    [Fact]
    public void RewritesIdsAndCollapseReferences()
    {
        string html = HtmlSanitizer.Sanitize(
            "<div id=\"foo\"><a href=\"#foo\">x</a>"
            + "<button data-toggle=\"collapse\" data-target=\"#foo\" "
            + "aria-controls=\"foo\" aria-expanded=\"true\">b</button></div>",
            "p");

        Assert.Contains("id=\"p-1\"", html);
        Assert.Contains("href=\"#p-1\"", html);
        Assert.Contains("data-target=\"#p-1\"", html);
        Assert.Contains("aria-controls=\"p-1\"", html);
        Assert.Contains("aria-expanded=\"true\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void FiltersClassesStylesLinksAndUnknownTags()
    {
        string html = HtmlSanitizer.Sanitize(
            "<section><span class=\"alert alert-danger evil\" "
            + "style=\"color:red;font-weight:700;padding: 1px\">x</span></section>"
            + "<a href=\"javascript:alert(1)\">bad</a>"
            + "<a href=\"https://example.com\">ok</a>",
            "p");

        Assert.DoesNotContain("section", html);
        Assert.Contains("class=\"alert alert-danger\"", html);
        Assert.Contains("style=\"font-weight:700;padding:1px\"", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("<a rel=\"noopener noreferrer\">bad</a>", html);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void KeepsSafeMarkdownMarkup()
    {
        string markdown = MarkdownLite.Render("**bold** [x](https://a.b)");
        string html = HtmlSanitizer.Sanitize(markdown, "p");

        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("href=\"https://a.b\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }
}
