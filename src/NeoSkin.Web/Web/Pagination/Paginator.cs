namespace NeoSkin.Web.Web.Pagination;

/// <summary>
/// <c>Illuminate\Pagination\LengthAwarePaginator</c> 的 JSON 分页链接子集
/// （<c>toArray()['links']</c> + <c>UrlWindow</c>）。
/// </summary>
public static class Paginator
{
    /// <summary>
    /// <c>$paginator-&gt;links()</c>：<c>[{url,label,active}]</c>（previous / 页码窗口 / next，
    /// 省略处为 <c>label="..."</c> 且 <c>url=null</c>）。
    /// </summary>
    public static List<Dictionary<string, object?>> Links(string path, int current, int lastPage,
        int onEachSide = 3)
    {
        current = Math.Max(current, 1);
        lastPage = Math.Max(lastPage, 1);
        var output = new List<Dictionary<string, object?>>();

        output.Add(Link(current > 1 ? JsonPageUrl(path, current - 1) : null,
            "&laquo; Previous", false));

        List<SortedDictionary<int, string>?> window = UrlWindow(path, current, lastPage, onEachSide);
        if (window[0] is not null)
        {
            AppendLinks(output, path, window[0]!, current);
        }

        if (window[1] is not null)
        {
            output.Add(Link(null, "...", false));
            AppendLinks(output, path, window[1]!, current);
        }

        if (window[2] is not null)
        {
            output.Add(Link(null, "...", false));
            AppendLinks(output, path, window[2]!, current);
        }

        output.Add(Link(current < lastPage ? JsonPageUrl(path, current + 1) : null,
            "Next &raquo;", false));

        return output;
    }

    /// <summary><c>UrlWindow::make()</c>：[first, slider, last]，每段可为 null。</summary>
    public static List<SortedDictionary<int, string>?> UrlWindow(string path, int current,
        int lastPage, int onEachSide = 3)
    {
        var window = new List<SortedDictionary<int, string>?>(3);

        if (lastPage < (onEachSide * 2) + 8)
        {
            window.Add(Range(path, 1, lastPage));
            window.Add(null);
            window.Add(null);
            return window;
        }

        int width = onEachSide + 4;

        if (current <= width)
        {
            window.Add(Range(path, 1, width + onEachSide));
            window.Add(null);
            window.Add(Range(path, lastPage - 1, lastPage));
            return window;
        }

        if (current > lastPage - width)
        {
            window.Add(Range(path, 1, 2));
            window.Add(null);
            window.Add(Range(path, lastPage - (width + (onEachSide - 1)), lastPage));
            return window;
        }

        window.Add(Range(path, 1, 2));
        window.Add(Range(path, current - onEachSide, current + onEachSide));
        window.Add(Range(path, lastPage - 1, lastPage));
        return window;
    }

    /// <summary>
    /// <c>LengthAwarePaginator::toArray()</c>（不含 data）：PHP 的分页 JSON 契约。
    /// </summary>
    public static Dictionary<string, object?> Body(string path, int page, int perPage,
        long total)
    {
        page = Math.Max(page, 1);
        perPage = Math.Max(perPage, 1);
        int lastPage = (int)Math.Max(Math.Ceiling(total / (double)perPage), 1);
        long from = total == 0 ? 0 : (long)(page - 1) * perPage + 1;
        long to = total == 0 ? 0 : Math.Min(total, (long)page * perPage);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["current_page"] = page,
            ["first_page_url"] = JsonPageUrl(path, 1),
            ["from"] = total == 0 ? null : from,
            ["last_page"] = lastPage,
            ["last_page_url"] = JsonPageUrl(path, lastPage),
            ["links"] = Links(path, page, lastPage),
            ["next_page_url"] = page < lastPage ? JsonPageUrl(path, page + 1) : null,
            ["path"] = path,
            ["per_page"] = perPage,
            ["prev_page_url"] = page > 1 ? JsonPageUrl(path, page - 1) : null,
            ["to"] = total == 0 ? null : to,
            ["total"] = total,
        };
    }

    private static void AppendLinks(List<Dictionary<string, object?>> output, string path,
        SortedDictionary<int, string> range, int current)
    {
        foreach ((int page, _) in range)
        {
            output.Add(Link(JsonPageUrl(path, page), page.ToString(), page == current));
        }
    }

    private static SortedDictionary<int, string> Range(string path, int start, int end)
    {
        start = Math.Max(start, 1);
        var range = new SortedDictionary<int, string>();
        for (int page = start; page <= end; page++)
        {
            range[page] = JsonPageUrl(path, page);
        }

        return range;
    }

    private static Dictionary<string, object?> Link(string? url, string label, bool active) =>
        new(StringComparer.Ordinal)
        {
            ["url"] = url,
            ["label"] = label,
            ["active"] = active,
        };

    /// <summary><c>$paginator-&gt;links()</c> 的 HTML 形态（分页组件）。</summary>
    public static string LinksHtml(string path, int current, int lastPage, int onEachSide = 3)
    {
        current = Math.Max(current, 1);
        lastPage = Math.Max(lastPage, 1);
        if (lastPage <= 1)
        {
            return "";
        }

        var html = new System.Text.StringBuilder();
        html.Append("<nav>\n        <ul class=\"pagination\">\n");

        if (current <= 1)
        {
            html.Append("            <li class=\"page-item disabled\" aria-disabled=\"true\" ")
                .Append("aria-label=\"Previous\">\n                <span class=\"page-link\" ")
                .Append("aria-hidden=\"true\">&lsaquo;</span>\n            </li>\n");
        }
        else
        {
            html.Append("            <li class=\"page-item\">\n                <a ")
                .Append("class=\"page-link\" href=\"")
                .Append(Html(JsonPageUrl(path, current - 1)))
                .Append("\" rel=\"prev\" aria-label=\"Previous\">&lsaquo;</a>\n")
                .Append("            </li>\n");
        }

        List<SortedDictionary<int, string>?> window =
            UrlWindow(path, current, lastPage, onEachSide);
        AppendPageRange(html, window[0], current);
        if (window[1] is not null)
        {
            html.Append("            <li class=\"page-item disabled\" aria-disabled=\"true\">")
                .Append("<span class=\"page-link\">...</span></li>\n");
            AppendPageRange(html, window[1], current);
        }

        if (window[2] is not null)
        {
            html.Append("            <li class=\"page-item disabled\" aria-disabled=\"true\">")
                .Append("<span class=\"page-link\">...</span></li>\n");
            AppendPageRange(html, window[2], current);
        }

        if (current < lastPage)
        {
            html.Append("            <li class=\"page-item\">\n                <a ")
                .Append("class=\"page-link\" href=\"")
                .Append(Html(JsonPageUrl(path, current + 1)))
                .Append("\" rel=\"next\" aria-label=\"Next\">&rsaquo;</a>\n")
                .Append("            </li>\n");
        }
        else
        {
            html.Append("            <li class=\"page-item disabled\" aria-disabled=\"true\" ")
                .Append("aria-label=\"Next\">\n                <span class=\"page-link\" ")
                .Append("aria-hidden=\"true\">&rsaquo;</span>\n            </li>\n");
        }

        html.Append("        </ul>\n    </nav>");
        return html.ToString();
    }

    private static void AppendPageRange(System.Text.StringBuilder html,
        SortedDictionary<int, string>? range, int current)
    {
        if (range is null)
        {
            return;
        }

        foreach ((int page, string url) in range)
        {
            if (page == current)
            {
                html.Append("            <li class=\"page-item active\" aria-current=\"page\">")
                    .Append("<span class=\"page-link\">").Append(page)
                    .Append("</span></li>\n");
            }
            else
            {
                html.Append("            <li class=\"page-item\"><a class=\"page-link\" href=\"")
                    .Append(Html(url)).Append("\">").Append(page).Append("</a></li>\n");
            }
        }
    }

    private static string Html(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    private static string JsonPageUrl(string path, int page) => path + "?page=" + page;
}
