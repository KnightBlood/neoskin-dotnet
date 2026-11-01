using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web;
using NeoSkin.Web.Web;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 路由表与匹配语义测试：174 条、Laravel 的 404/405/OPTIONS、路径参数。
/// </summary>
public sealed class RoutesTest
{
    private static readonly Routes Routes = NeoSkin.Web.Web.Routes.Load();

    [Fact]
    public void LoadsAllRoutesInOrder()
    {
        Assert.Equal(174, Routes.Count);

        // 第一条与最后一条锁定顺序（routes.json 的注册顺序 = Laravel 匹配顺序）
        Assert.Equal(".well-known/change-password", Routes.All[0].Uri);
        Assert.Equal("/", Routes.All[1].Uri);
        Assert.Equal("home", Routes.All[1].Name);

        // 源码里 codegraph 漏记的上传路由
        RouteDefinition upload = Routes.All.Single(r => r.Uri == "skinlib" && r.Methods.Contains("post"));
        Assert.Equal("skinlib.upload.submit", upload.Name);
        Assert.Contains("authorize", upload.Middleware);
        Assert.Contains("verified", upload.Middleware);
    }

    [ConformanceFact]
    public void MatchesConformanceRoutes()
    {
        Corpus corpus = Corpus.Require("routes");
        int checkedCases = 0;

        foreach (JsonNode c in corpus.Cases())
        {
            if (Corpus.Text(c, "op") == "summary")
            {
                continue;
            }

            // 语料 id 形如 "GET|HEAD auth/login"
            string id = Corpus.TextOrEmpty(c, "id");
            int space = id.IndexOf(' ');
            string methods = id[..space];
            string uri = id[(space + 1)..];

            RouteDefinition? route = Routes.All.FirstOrDefault(r =>
                r.Uri == uri && string.Join("|", r.Methods.Select(m => m.ToUpperInvariant()))
                    == methods);
            Assert.NotNull(route);
            Assert.Equal(Corpus.Text(c, "name") ?? "", route!.Name ?? "");
            AssertNormalized(Corpus.TextOrEmpty(c, "action"), route.Action);
            checkedCases++;
        }

        Assert.True(checkedCases >= 173, "语料路由条数变了？");
    }

    /// <summary>语料里的 action 带了 PHP 风格的转义反斜杠（<c>\\</c>），比较前归一化成单个。</summary>
    private static void AssertNormalized(string expected, string actual)
    {
        static string Normalize(string value) => value.Replace("\\\\", "\\", StringComparison.Ordinal).TrimStart('\\');
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public void RouteListJsonMatchesArtisanShape()
    {
        string json = NeoSkinApp.RouteListJson(Routes);
        JsonNode array = JsonNode.Parse(json)!;
        Assert.Equal(174, array.AsArray().Count);
        Assert.Equal("/", array[1]!["uri"]!.GetValue<string>());
        Assert.Equal("home", array[1]!["name"]!.GetValue<string>());
        Assert.Null(array[0]!["name"]);
    }

    // ================================================================== 匹配语义

    [Fact]
    public void MatchesPathAndMethod()
    {
        RouteTable table = new(Routes);

        Assert.NotNull(table.Match("GET", "/"));
        Assert.NotNull(table.Match("GET", "auth/login"));
        Assert.NotNull(table.Match("POST", "auth/login"));
        Assert.Null(table.Match("PUT", "auth/login"));
        Assert.Null(table.Match("GET", "no-such-route"));

        // 尾斜杠任意数量都容忍
        Assert.NotNull(table.Match("GET", RouteTable.LaravelPath("/auth/login///")));
    }

    [Fact]
    public void ExtractsParameters()
    {
        RouteTable table = new(Routes);

        RouteMatch? avatar = table.Match("GET", "avatar/123");
        Assert.NotNull(avatar);
        Assert.Equal("123", avatar!.Parameters["tid"]);

        RouteMatch? player = table.Match("GET", "Steve.json");
        Assert.NotNull(player);
        Assert.Equal("Steve", player!.Parameters["player"]);

        RouteMatch? csl = table.Match("GET", "csl/Alex.json");
        Assert.NotNull(csl);
        Assert.Equal("Alex", csl!.Parameters["player"]);
    }

    [Fact]
    public void AllowedMethodsFollowLaravelVerbOrder()
    {
        RouteTable table = new(Routes);
        Assert.Equal(["GET", "HEAD", "POST"], table.AllowedMethods("auth/login"));
        Assert.Equal(["GET", "HEAD"], table.AllowedMethods("/"));
        Assert.Empty(table.AllowedMethods("no-such-route"));
    }

    [Fact]
    public void DetectsDoubleSlashAndLaravelPath()
    {
        Assert.True(RouteTable.HasInnerDoubleSlash("/auth//login"));
        Assert.True(RouteTable.HasInnerDoubleSlash("//auth/login"));
        Assert.False(RouteTable.HasInnerDoubleSlash("/auth/login//"));

        Assert.Equal("auth/login", RouteTable.LaravelPath("/auth/login//"));
        Assert.Equal("/", RouteTable.LaravelPath(""));
        Assert.Equal("/", RouteTable.LaravelPath("///"));
    }
}
