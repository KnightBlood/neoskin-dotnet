using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Storage;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web.Pagination;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\ReportController</c> 的移植。
///
/// <p>已实现：<c>submit</c>（举报材质）、<c>manage</c>（管理端列表，走搜索串）、
/// <c>review</c>（拒绝/删除/封禁）。<c>track</c>（我的举报 HTML）等 Phase 7。</p>
/// </summary>
public sealed class ReportController(
    Option option,
    UserRepository users,
    TextureRepository textures,
    PlayerRepository players,
    ClosetRepository closet,
    ReportRepository reports,
    Database db,
    string texturesDirectory,
    Config.NeoSkinSettings settings)
{
    private readonly ITextureStorage _storage = new LocalTextureStorage(texturesDirectory);

    /// <summary><c>POST skinlib/report</c>。</summary>
    public RequestHandler Submit() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? reporter = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (reporter is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("tid", "required");
        v.AddRule("tid", "exists:textures");
        v.AddRule("reason", "required");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        int tid = RequestInput.Int(input, "tid");
        string reason = RequestInput.String(input, "reason");

        if (reports.CountByReporterAndTid(reporter.Uid, tid) > 0)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.report.duplicate")));
            return;
        }

        int scoreChange = option.GetInt("reporter_score_modification", 0);
        if (scoreChange < 0 && reporter.Score < -scoreChange)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.upload.lack-score")));
            return;
        }

        users.UpdateScore(reporter.Uid, reporter.Score + scoreChange);

        TextureRow? texture = textures.FindByTid(tid);
        long id = reports.Insert(tid, texture?.Uploader ?? 0, reporter.Uid, reason,
            ReportRepository.Pending, PhpDate.Now("Asia/Shanghai"));
        if (id <= 0)
        {
            context.Response.StatusCode = 500;
            return;
        }

        await JsonAsync(context, 200, Message(0, Trans(context, "skinlib.report.success")));
    };

    /// <summary><c>GET admin/reports/list</c> / <c>GET api/admin/reports</c>。</summary>
    public RequestHandler Manage() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string q = RequestInput.String(input, "q");
        int page = Math.Max(1, RequestInput.Int(input, "page"));

        SearchString.Result compiled = SearchString.Compile(q, ReportSearchOptions());
        ReportRepository.AdminPageResult result = reports.AdminPage(compiled, page, 9);

        string path = "http://" + HostOf(context) + (context.Request.Path.Value ?? "/");
        Dictionary<string, object?> body = Paginator.Body(path, page, 9, result.Total);
        body["data"] = result.Rows;
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>PUT admin/reports/{report}</c> / <c>PUT api/admin/reports/{report}</c>。</summary>
    public RequestHandler Review() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? auth = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);

        long reportId = parameters.TryGetValue("report", out string? raw) && long.TryParse(raw,
            out long parsed) ? parsed : 0;
        ReportRepository.Row? report = reportId == 0 ? null : reports.FindById(reportId);
        if (report is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("action", "required");
        v.AddRule("action", "in:delete,ban,reject");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string action = RequestInput.String(input, "action");

        if (action == "reject")
        {
            if (report.Status == ReportRepository.Pending)
            {
                int score = option.GetInt("reporter_score_modification", 0);
                UserRepository.UserRow? informer = users.FindById(report.Reporter);
                if (informer is not null && score > 0)
                {
                    users.UpdateScore(informer.Uid, informer.Score - score);
                }
            }

            reports.UpdateStatus(report.Id, ReportRepository.Rejected);
            await JsonAsync(context, 200, Message(0, Trans(context, "general.op-success"),
                new Dictionary<string, object?> { ["status"] = ReportRepository.Rejected }));
            return;
        }

        if (action == "delete")
        {
            TextureRow? texture = textures.FindByTid(report.Tid);
            if (texture is not null)
            {
                try
                {
                    _storage.Delete(texture.Hash);
                }
                catch (IOException)
                {
                    // Storage::delete 对不存在的文件同样不报错
                }

                textures.DeleteByTid(texture.Tid);
                TextureSideEffects.OnDeleted(db, option, texture, users, players, closet,
                    textures);
            }
            else
            {
                // 材质已被上传者删除：退还积分，但不给举报者奖励
                ReturnScore(report);
                reports.UpdateStatus(report.Id, ReportRepository.Resolved);
                await JsonAsync(context, 200,
                    Message(0, Trans(context, "general.texture-deleted"),
                        new Dictionary<string, object?> { ["status"] = ReportRepository.Resolved }));
                return;
            }
        }
        else if (action == "ban")
        {
            UserRepository.UserRow? uploader = users.FindById(report.Uploader);
            if (uploader is null)
            {
                await JsonAsync(context, 200,
                    Message(1, Trans(context, "admin.users.operations.non-existent")));
                return;
            }

            if (auth is null || auth.Permission <= uploader.Permission)
            {
                await JsonAsync(context, 200,
                    Message(1, Trans(context, "admin.users.operations.no-permission")));
                return;
            }

            users.UpdatePermission(uploader.Uid, UserRepository.Banned);
        }

        ReturnScore(report);
        GiveAward(report);
        reports.UpdateStatus(report.Id, ReportRepository.Resolved);
        await JsonAsync(context, 200, Message(0, Trans(context, "general.op-success"),
            new Dictionary<string, object?> { ["status"] = ReportRepository.Resolved }));
    };

    /// <summary><c>GET user/reports</c>：我的举报。</summary>
    public RequestHandler Track() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        int page = Math.Max(1, int.TryParse(context.Request.Query["page"], out int parsed)
            ? parsed
            : 1);
        ReportRepository.Page result = reports.PageByReporter(user.Uid, page, 10);
        string[] labels = StatusLabels(context);

        var rows = new List<object?>();
        foreach (ReportRepository.Row row in result.Rows)
        {
            string label = row.Status >= 0 && row.Status < labels.Length
                ? labels[row.Status]
                : "";
            rows.Add(new PhpArray()
                .With("tid", row.Tid)
                .With("reason", row.Reason)
                .With("status", row.Status)
                .With("status_label", label)
                .With("report_at", row.ReportAt)
                .With("show_url", "/skinlib/show/" + row.Tid));
        }

        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), host, context.Request.Path.Value ?? "/user/reports",
            settings.Debug, "user", session);
        vars["pageTitle"] = Trans(context, "general.my-reports");
        vars["errors"] = TemplateErrors.Empty;
        vars["reports"] = rows;
        int last = (int)Math.Max(1, (result.Total + 9) / 10);
        vars["reports_links"] = Paginator.LinksHtml(
            "http://" + host + "/user/reports", page, last);
        string html = TemplateRenderer.Render("user/report", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };

    private static string[] StatusLabels(HttpContext context)
    {
        object? raw = Translator(context).Get("front-end.report.status", null, Locale(context));
        if (raw is PhpArray array)
        {
            string[] values = array.Values.Select(PhpString.Cast).ToArray();
            return values.Length >= 3 ? values : ["待处理", "已受理", "已拒绝"];
        }

        return ["待处理", "已受理", "已拒绝"];
    }
    // ================================================================== 辅助

    private static SearchString.Options ReportSearchOptions() =>
        new SearchString.Options()
            .Column("id")
            .Column("tid")
            .Column("uploader")
            .Column("reporter")
            .Column("reason")
            .Column("status")
            .Date("report_at")
            .Keyword("sort", "sort")
            .Keyword("order_by", "order_by")
            .Keyword("fields", "select")
            .Keyword("limit", "limit")
            .Keyword("from", "offset");

    private void ReturnScore(ReportRepository.Row report)
    {
        int score = option.GetInt("reporter_score_modification", 0);
        if (report.Status == ReportRepository.Pending && score < 0)
        {
            UserRepository.UserRow? informer = users.FindById(report.Reporter);
            if (informer is not null)
            {
                users.UpdateScore(informer.Uid, informer.Score - score);
            }
        }
    }

    private void GiveAward(ReportRepository.Row report)
    {
        if (report.Status != ReportRepository.Pending)
        {
            return;
        }

        UserRepository.UserRow? informer = users.FindById(report.Reporter);
        if (informer is not null)
        {
            users.UpdateScore(informer.Uid,
                informer.Score + option.GetInt("reporter_reward_score", 0));
        }
    }

    private UserRepository.UserRow? SessionUser(HttpContext context)
    {
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        return session is null ? null : new Auth.SessionGuard(session, users).User();
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string HostOf(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;

    private static Dictionary<string, object?> Message(int code, string message) =>
        new(StringComparer.Ordinal) { ["code"] = code, ["message"] = message };

    private static Dictionary<string, object?> Message(int code, string message,
        Dictionary<string, object?> data) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["message"] = message,
            ["data"] = data,
        };

    private static async Task ValidationFailedAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        await JsonAsync(context, 422, new Dictionary<string, object?>
        {
            ["message"] = Validator.Summarize(errors),
            ["errors"] = errors,
        });
    }

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
