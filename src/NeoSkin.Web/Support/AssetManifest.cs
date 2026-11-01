namespace NeoSkin.Web.Support;

/// <summary>
/// 前端资源路径表（源 <c>resources/views/assets/*.twig</c> 的构建产物）。
///
/// <p>生产环境用哈希文件名（与源仓库 <c>public/app/</c> 的实际产物一致）；
/// <c>debug=true</c> 时指向 webpack dev-server（文件名无哈希）。</p>
/// </summary>
public static class AssetManifest
{
    public const string DevRoot = "//localhost:8080/app/";

    private const string ProdApp = "app.7278b90.js";
    private const string ProdStyle = "style.1dd8a8a.css";
    private const string ProdHome = "home.fe8cbb3.js";
    private const string ProdHomeCss = "home-css.6c76352.css";
    private const string ProdSpectre = "spectre.e885dd0.css";

    private const string DevApp = "app.js";
    private const string DevStyle = "style.css";
    private const string DevHome = "home.js";
    private const string DevHomeCss = "home-css.css";
    private const string DevSpectre = "spectre.css";

    /// <summary>生成 <c>app / style / home / home_css / spectre</c> 五个 URL。</summary>
    public static IReadOnlyDictionary<string, string> Urls(bool debug, string? cdnBase)
    {
        if (debug)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app"] = DevRoot + DevApp,
                ["style"] = DevRoot + DevStyle,
                ["home"] = DevRoot + DevHome,
                ["home_css"] = DevRoot + DevHomeCss,
                ["spectre"] = DevRoot + DevSpectre,
            };
        }

        string root = (cdnBase ?? "") + "/app/";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app"] = root + ProdApp,
            ["style"] = root + ProdStyle,
            ["home"] = root + ProdHome,
            ["home_css"] = root + ProdHomeCss,
            ["spectre"] = root + ProdSpectre,
        };
    }
}
