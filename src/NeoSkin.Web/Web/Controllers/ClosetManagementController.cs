using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\ClosetManagementController</c> 的移植。
///
/// <p>管理员查看 / 添加 / 移除指定用户的衣柜。</p>
/// </summary>
public sealed class ClosetManagementController(
    UserRepository users,
    TextureRepository textures,
    ClosetRepository closet)
{
    /// <summary><c>GET admin/closet/{user}</c>：<c>$user-&gt;closet</c>（含 pivot）。</summary>
    public RequestHandler List() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = TargetUser(context, parameters);
        if (user is null)
        {
            return;
        }

        var items = new List<Dictionary<string, object?>>();
        foreach (ClosetRepository.Item item in closet.All(user.Uid))
        {
            Dictionary<string, object?> texture = ClosetRepository.TextureJson(item.Texture);
            texture.Remove("model");
            texture["pivot"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user_uid"] = item.UserUid,
                ["texture_tid"] = item.TextureTid,
                ["item_name"] = item.ItemName,
            };
            items.Add(texture);
        }

        await JsonAsync(context, 200, items);
    };

    /// <summary><c>POST admin/closet/{user}</c>。</summary>
    public RequestHandler Add() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = TargetUser(context, parameters);
        if (user is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        long tid = RequestInput.Int(input, "tid");
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "user.closet.add.not-found")));
            return;
        }

        if (closet.Exists(user.Uid, tid))
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "user.closet.add.repeated")));
            return;
        }

        closet.Attach(user.Uid, texture.Tid, texture.Name);
        await JsonAsync(context, 200, Message(0, "", Data(user, texture)));
    };

    /// <summary><c>DELETE admin/closet/{user}</c>。</summary>
    public RequestHandler Remove() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = TargetUser(context, parameters);
        if (user is null)
        {
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        long tid = RequestInput.Int(input, "tid");
        if (closet.Find(user.Uid, tid) is null)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.closet.remove.non-existent")));
            return;
        }

        closet.Detach(user.Uid, tid);
        TextureRow? texture = textures.FindByTid(tid);
        await JsonAsync(context, 200, Message(0, "", Data(user, texture)));
    };

    // ================================================================== 辅助

    private UserRepository.UserRow? TargetUser(HttpContext context,
        IReadOnlyDictionary<string, string> parameters)
    {
        int uid = parameters.TryGetValue("user", out string? raw) ? RequestInput.ToInt(raw) : 0;
        UserRepository.UserRow? user = users.FindById(uid);
        if (user is null)
        {
            context.Response.StatusCode = 404;
        }

        return user;
    }

    private Dictionary<string, object?> Data(UserRepository.UserRow user, TextureRow? texture)
    {
        Dictionary<string, object?>? textureJson = texture is null
            ? null
            : ClosetRepository.TextureJson(texture);
        textureJson?.Remove("model");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user"] = users.FindJsonById(user.Uid),
            ["texture"] = textureJson,
        };
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

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

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
