using NeoSkin.Web.Support;
using NeoSkin.Web.View;

namespace NeoSkin.Tests.View;

/// <summary>
/// Thymeleaf 子集渲染器的单元/集成测试：表达式、th:* 方言、片段继承，
/// 以及源项目真实模板（copyright / home）的渲染。
/// </summary>
public sealed class TemplateEngineTest
{
    private static readonly string ViewsRoot =
        Path.Combine(AppContext.BaseDirectory, "Resources", "views");

    private static TemplateEngine Memory(params (string Name, string Source)[] templates) =>
        new(new TemplateRepository(new DictionaryTemplateSource(
            templates.ToDictionary(t => t.Name, t => t.Source))));

    private static TemplateContext Context(string template,
        params (string Key, object? Value)[] variables)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, object? value) in variables)
        {
            dict[key] = value;
        }

        return TemplateContext.Root(template, dict);
    }

    private static object Eval(string expression,
        params (string Key, object? Value)[] variables) =>
        TemplateExpression.Parse(expression)
            .Evaluate(Context("test", variables))!;

    [Fact]
    public void ExpressionArithmeticAndLogic()
    {
        Assert.Equal(3L, Eval("1 + 2"));
        Assert.Equal(6L, Eval("2 * 3"));
        Assert.Equal(2L, Eval("7 % 5"));
        Assert.True((bool)Eval("1 < 2 and 3 != 4"));
        Assert.False((bool)Eval("1 > 2 or false"));
        Assert.Equal("b", Eval("true ? 'a' == 'a' ? 'b' : 'c' : 'd'"));
    }

    [Fact]
    public void ExpressionMembersIndexesAndMethods()
    {
        var map = new PhpArray().With("a", 1L).With("nested", new PhpArray().With("x", "y"));
        var errors = new FakeErrors(["bad"]);

        Assert.Equal("y", Eval("m['nested'].x", ("m", map)));
        Assert.Equal(1L, Eval("m.a", ("m", map)));
        Assert.True((bool)Eval("errors.any", ("errors", errors)));
        Assert.True((bool)Eval("errors.any()", ("errors", errors)));
        Assert.Equal("bad", Eval("errors.first()", ("errors", errors)));
        Assert.True((bool)Eval("#lists.isEmpty(items)", ("items", new List<string>())));
        Assert.Equal("a b", Eval("#strings.listJoin(items, ' ')",
            ("items", new List<string> { "a", "b" })));
    }

    [Fact]
    public void LiteralSubstitutionAndEscaping()
    {
        const string Template = """
            <p th:text="|${a} - ${b}|"></p>
            <p th:utext="${html}"></p>
            """;
        TemplateEngine engine = Memory(("t", Template));
        string html = engine.Render("t", Context("t", ("a", "A"), ("b", 2),
            ("html", "<b>x</b>")));

        Assert.Contains("<p>A - 2</p>", html);
        Assert.Contains("<p><b>x</b></p>", html);
    }

    [Fact]
    public void IfUnlessEachAndStat()
    {
        const string Template = """
            <ul>
              <li th:each="item, stat : ${items}" th:if="${stat.index != 1}"
                  th:classappend="${stat.first ? 'first' : ''}"
                  th:text="|${stat.count}:${item}|"></li>
            </ul>
            <p th:unless="${empty}">shown</p>
            """;
        TemplateEngine engine = Memory(("t", Template));
        string html = engine.Render("t", Context("t",
            ("items", new List<string> { "a", "b", "c" }), ("empty", true)));

        Assert.Contains("<li class=\"first\">1:a</li>", html);
        Assert.DoesNotContain("2:b", html);
        Assert.Contains("<li>3:c</li>", html);
        Assert.DoesNotContain("shown", html);
    }

    [Fact]
    public void FragmentInheritanceAndArguments()
    {
        const string Base = """
            <!DOCTYPE html>
            <html th:fragment="page(title, content, before)" th:lang="${locale}">
            <head><title th:text="|${title} - ${site_name}|"></title></head>
            <body class="a" th:classappend="${dark ? 'dark' : ''}">
            <th:block th:replace="${content}"></th:block>
            <th:block th:replace="${before}"></th:block>
            </body>
            </html>
            """;
        const string Page = """
            <!DOCTYPE html>
            <html th:replace="~{mini/base :: page(${pageTitle}, ~{::content}, ~{::before})}">
            <body>
              <th:block th:fragment="content">
                <ul><li th:each="item, stat : ${items}"
                        th:text="|${stat.count}. ${item} - ${view.trans('k')}|"></li></ul>
              </th:block>
              <th:block th:fragment="before">
                <script th:inline="javascript">var b = [[${blessing}]];</script>
              </th:block>
            </body>
            </html>
            """;

        TemplateEngine engine = Memory(("mini/base", Base), ("mini/page", Page));
        var view = new TemplateViewContext(null, null, "zh_CN");
        string html = engine.Render("mini/page", Context("mini/page",
            ("pageTitle", "My Page"), ("site_name", "Site"), ("dark", true),
            ("locale", "zh_CN"), ("items", new List<string> { "a", "b" }),
            ("blessing", new PhpArray().With("version", "1")), ("view", view)));

        Assert.Contains("<title>My Page - Site</title>", html);
        Assert.Contains("<html lang=\"zh_CN\">", html);
        Assert.Contains("<body class=\"a dark\">", html);
        Assert.Contains("<li>1. a - k</li>", html);
        Assert.Contains("<li>2. b - k</li>", html);
        Assert.Contains("var b = {\"version\":\"1\"};", html);
        Assert.Equal(1, CountOccurrences(html, "<!DOCTYPE html>"));
    }

    [Fact]
    public void SwitchCase()
    {
        const string Template = """
            <th:block th:switch="${value}">
              <th:block th:case="0">zero</th:block>
              <th:block th:case="1">one</th:block>
              <th:block th:case="*">other</th:block>
            </th:block>
            """;
        TemplateEngine engine = Memory(("t", Template));

        Assert.Contains("one", engine.Render("t", Context("t", ("value", 1L))));
        Assert.DoesNotContain("zero", engine.Render("t", Context("t", ("value", 1L))));
        Assert.Contains("other", engine.Render("t", Context("t", ("value", 9L))));
    }

    [Fact]
    public void AttributeProcessors()
    {
        const string Template = """
            <div class="box" th:classappend="|c-${n}|"
                 th:attr="data-x=${n},data-y=|v-${n}|,disabled=${off ? 'disabled' : null}">
              <a th:href="${url}">x</a>
              <option th:selected="${picked}">o</option>
            </div>
            """;
        TemplateEngine engine = Memory(("t", Template));
        string html = engine.Render("t", Context("t", ("n", 2L), ("off", true),
            ("url", "/a?x=1&y=2"), ("picked", true)));

        Assert.Contains("class=\"box c-2\"", html);
        Assert.Contains("data-x=\"2\"", html);
        Assert.Contains("data-y=\"v-2\"", html);
        Assert.Contains("disabled=\"disabled\"", html);
        Assert.Contains("href=\"/a?x=1&amp;y=2\"", html);
        Assert.Contains("selected=\"selected\"", html);
    }

    [Fact]
    public void RealCopyrightFragment()
    {
        var engine = new TemplateEngine(new TemplateRepository(
            new FileTemplateSource(ViewsRoot)));
        var view = new TemplateViewContext(null, null, "zh_CN");

        string c0 = engine.RenderFragment(
            new TemplateFragmentRef("shared/copyright", "shared-copyright",
                [0L]), Context("shared/copyright", ("view", view), ("copyright", 0L)));
        string c5 = engine.RenderFragment(
            new TemplateFragmentRef("shared/copyright", "shared-copyright",
                [5L]), Context("shared/copyright", ("view", view), ("copyright", 5L)));
        string c9 = engine.RenderFragment(
            new TemplateFragmentRef("shared/copyright", "shared-copyright",
                [9L]), Context("shared/copyright", ("view", view), ("copyright", 9L)));

        Assert.Contains("Powered with", c0);
        Assert.Contains("使用", c5);
        Assert.Contains("自豪地采用", c9);
        Assert.Contains("id=\"copyright-text\"", c9);
        Assert.DoesNotContain("th:switch", c9);
    }

    [Fact]
    public void RealHomeTemplateRenders()
    {
        var engine = new TemplateEngine(new TemplateRepository(
            new FileTemplateSource(ViewsRoot)));
        var view = new TemplateViewContext(null, null, "zh_CN", "csrf-token");

        var seo = new PhpArray()
            .With("keywords", null)
            .With("description", null)
            .With("extra", null);
        var assets = new PhpArray()
            .With("style", "/app/style.css")
            .With("home_css", "/app/home.css")
            .With("home", "/app/home.js")
            .With("app", "/app/app.js");

        string html = engine.Render("home", Context("home",
            ("locale", "zh_CN"),
            ("site_name", "Blessing Skin"),
            ("site_description", "A skin server"),
            ("navbar_color", "cyan"),
            ("color_mode", "dark"),
            ("dark_mode", false),
            ("favicon", "/app/favicon.ico"),
            ("theme_color", "#17a2b8"),
            ("seo", seo),
            ("links", new List<object?>()),
            ("inline_css", null),
            ("extra_head", null),
            ("has_cdn_host", false),
            ("custom_cdn_host", null),
            ("blessing", new PhpArray()
                .With("version", "6.0.11_patch")
                .With("locale", "zh_CN")
                .With("base_url", "/")
                .With("site_name", "Blessing Skin")),
            ("assets", assets),
            ("i18n", "/lang/zh_CN.js"),
            ("scripts", new List<object?>()),
            ("inline_js", null),
            ("extra_foot", null),
            ("copyright", 0L),
            ("custom_copyright", null),
            ("fixed_bg", false),
            ("home_pic_url", "/pic.jpg"),
            ("transparent_navbar", false),
            ("hide_intro", false),
            ("featureItems", new List<object?> { "a", "b" }),
            ("intro", "intro text"),
            ("blessingExtra", "{}"),
            ("current", "简体中文"),
            ("langs", new List<object?>()),
            ("view", view)));

        Assert.Contains("<title>Blessing Skin</title>", html);
        Assert.Contains("name=\"csrf-token\" content=\"csrf-token\"", html);
        Assert.Contains("window.blessing = {\"version\":\"6.0.11_patch\",",
            html);
        Assert.Contains("A skin server", html);
        Assert.Contains("/app/app.js", html);
        Assert.Contains("Powered with", html);
        Assert.DoesNotContain("th:", html.Replace("xmlns:th", ""));
        Assert.DoesNotContain("${", html);
    }

    [Fact]
    public void RealUserIndexRendersGridWidgets()
    {
        var engine = new TemplateEngine(new TemplateRepository(
            new FileTemplateSource(ViewsRoot)));
        var vars = BaseVars();
        vars["scope"] = "user";
        vars["pageTitle"] = "仪表盘";
        vars["announcement"] = "<p>公告正文</p>";
        vars["extra_json"] = "{}";
        vars["rates"] = new PhpArray()
            .With("storage", 1L).With("player", 2L).With("closet", 3L);
        vars["gridRows"] = new List<object?>
        {
            new List<object?>
            {
                Column("md-7", "user/widgets/email-verification",
                    "user/widgets/dashboard/usage"),
                Column("md-5", "user/widgets/dashboard/announcement"),
            },
        };

        string html = engine.Render("user/index", TemplateContext.Root("user/index", vars));

        Assert.Contains("class=\"col-md-7\"", html);
        Assert.Contains("class=\"col-md-5\"", html);
        Assert.Contains("id=\"email-verification\"", html);
        Assert.Contains("id=\"usage-box\"", html);
        Assert.Contains("公告正文", html);
        Assert.Contains("id=\"blessing-extra\"", html);
        Assert.DoesNotContain("${", html);
    }

    [Fact]
    public void RealAdminCommunicationsListRenders()
    {
        var engine = new TemplateEngine(new TemplateRepository(
            new FileTemplateSource(ViewsRoot)));
        var vars = BaseVars();
        vars["scope"] = "admin";
        vars["pageTitle"] = "通知与公告";
        vars["editor_open"] = false;
        var item = new PhpArray()
            .With("id", 1L)
            .With("title", "标题一")
            .With("kind", "message")
            .With("status", "published")
            .With("read_count", 0L)
            .With("delivery_count", 2L)
            .With("updated_at", "2026-10-30 00:00:00")
            .With("edit_url", "/admin/communications?edit=1")
            .With("withdraw_url", "/admin/communications/1/withdraw");
        vars["messages"] = new List<object?> { item };
        vars["pagination"] = new PhpArray()
            .With("total", 1L)
            .With("current_page", 1L)
            .With("last_page", 1L)
            .With("on_first_page", true)
            .With("has_more_pages", false)
            .With("previous_page_url", null)
            .With("next_page_url", null)
            .With("pages", new List<object?>());

        string html = engine.Render("admin/communications",
            TemplateContext.Root("admin/communications", vars));

        Assert.Contains("站内信发布与草稿记录", html);
        Assert.Contains("标题一", html);
        Assert.Contains("已发布", html);
        Assert.Contains("data-title=\"标题一\"", html);
        Assert.Contains("共 1 条", html);
        Assert.DoesNotContain("${", html);
    }

    private static PhpArray Column(string css, params string[] widgets) =>
        new PhpArray()
            .With("css", css)
            .With("widgets", widgets.ToList());

    private static Dictionary<string, object?> BaseVars()
    {
        var view = new TemplateViewContext(null, null, "zh_CN", "token");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["locale"] = "zh_CN",
            ["pageTitle"] = "标题",
            ["site_name"] = "Blessing Skin",
            ["site_description"] = "",
            ["navbar_color"] = "cyan",
            ["color_mode"] = "dark",
            ["dark_mode"] = false,
            ["favicon"] = "/app/favicon.ico",
            ["theme_color"] = "#17a2b8",
            ["seo"] = new PhpArray()
                .With("keywords", null).With("description", null).With("extra", null),
            ["links"] = new List<object?>(),
            ["inline_css"] = null,
            ["custom_cdn_host"] = null,
            ["has_cdn_host"] = false,
            ["blessing"] = new PhpArray().With("version", "6.0.11_patch"),
            ["extra_head"] = null,
            ["assets"] = new PhpArray()
                .With("style", "/app/style.css")
                .With("app", "/app/app.js")
                .With("home_css", "/app/home.css")
                .With("home", "/app/home.js"),
            ["i18n"] = "/lang/zh_CN.js",
            ["scripts"] = new List<object?>(),
            ["inline_js"] = null,
            ["extra_foot"] = null,
            ["copyright"] = 0L,
            ["custom_copyright"] = null,
            ["site_url"] = "",
            ["sidebar_color"] = "dark",
            ["user"] = null,
            ["panel_avatar"] = "",
            ["panel_avatar_png"] = "",
            ["panel_nickname"] = null,
            ["panel_email"] = null,
            ["badges"] = new List<object?>(),
            ["user_nickname"] = null,
            ["avatar"] = "",
            ["avatar_png"] = "",
            ["menu"] = new List<object?>(),
            ["userItems"] = new List<object?>(),
            ["exploreItems"] = new List<object?>(),
            ["adminItems"] = new List<object?>(),
            ["current"] = "简体中文",
            ["langs"] = new List<object?>(),
            ["notifications"] = new List<object?>(),
            ["notifications_json"] = "[]",
            ["errors"] = new FakeErrors([]),
            ["view"] = view,
        };
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed class FakeErrors(IReadOnlyList<string> messages) : ITemplateObject
    {
        public bool TryGetMember(string name, out object? value)
        {
            value = name switch
            {
                "any" => messages.Count > 0,
                "first" => messages.Count > 0 ? messages[0] : "",
                _ => null,
            };
            return value is not null;
        }

        public bool TryInvoke(string name, IReadOnlyList<object?> arguments,
            out object? value)
        {
            switch (name)
            {
                case "any":
                    value = messages.Count > 0;
                    return true;
                case "first":
                    value = messages.Count > 0 ? messages[0] : "";
                    return true;
                case "all":
                    value = messages;
                    return true;
                default:
                    value = null;
                    return false;
            }
        }
    }
}
