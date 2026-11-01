using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>MarkdownLite 的常用 GFM 子集。</summary>
public sealed class MarkdownLiteTest
{
    [Fact]
    public void HeadingsParagraphsAndLists()
    {
        string html = MarkdownLite.Render("# Title\n\nHello **world**\n\n- a\n- b");

        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<p>Hello <strong>world</strong></p>", html);
        Assert.Contains("<ul>\n<li>a</li>\n<li>b</li>\n</ul>", html);
    }

    [Fact]
    public void InlineCodeLinksAndImages()
    {
        string html = MarkdownLite.Render("`code` [link](https://a.b) ![alt](https://a.b/i.png)");

        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<a href=\"https://a.b\">link</a>", html);
        Assert.Contains("<img src=\"https://a.b/i.png\" alt=\"alt\" />", html);
    }

    [Fact]
    public void QuoteHrAndFence()
    {
        string html = MarkdownLite.Render("> quote\n\n---\n\n```\nx < y\n```");

        Assert.Contains("<blockquote>\n<p>quote</p>\n</blockquote>", html);
        Assert.Contains("<hr />", html);
        Assert.Contains("<pre><code>x &lt; y</code></pre>", html);
    }

    [Fact]
    public void EscapesHtml()
    {
        string html = MarkdownLite.Render("<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
