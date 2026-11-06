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

    /// <summary>
    /// 实际静态资源根目录（<c>IWebHostEnvironment.WebRootPath</c>）。
    /// 由 <see cref="NeoSkinApp.Create"/> 写入；未设置时退回 6.0.11 的固定哈希。
    /// </summary>
    public static string? WebRoot { get; set; }

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
            ["app"] = root + Pick("app.", ".js", ProdApp),
            ["style"] = root + Pick("style.", ".css", ProdStyle),
            ["home"] = root + Pick("home.", ".js", ProdHome),
            ["home_css"] = root + Pick("home-css.", ".css", ProdHomeCss),
            ["spectre"] = root + Pick("spectre.", ".css", ProdSpectre),
        };
    }

    /// <summary>
    /// 在真实 <c>wwwroot/app</c> 中挑选文件名：优先精确的 6.0.11 哈希，
    /// 否则取同前缀/后缀的产物。这样 CI 使用 6.0.10 发布包时不会 404。
    /// </summary>
    private static string Pick(string prefix, string suffix, string fallback)
    {
        if (string.IsNullOrEmpty(WebRoot))
        {
            return fallback;
        }

        string directory = Path.Combine(WebRoot, "app");
        if (!Directory.Exists(directory))
        {
            return fallback;
        }

        if (File.Exists(Path.Combine(directory, fallback)))
        {
            return fallback;
        }

        try
        {
            return Directory.EnumerateFiles(directory, prefix + "*" + suffix)
                .Select(Path.GetFileName)
                .Where(name => name is not null
                               && name.StartsWith(prefix, StringComparison.Ordinal)
                               && name.EndsWith(suffix, StringComparison.Ordinal))
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .FirstOrDefault() ?? fallback;
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
