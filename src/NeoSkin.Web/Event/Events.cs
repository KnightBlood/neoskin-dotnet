using NeoSkin.Web.Db;

namespace NeoSkin.Web.Event;

/// <summary>源项目 <c>App\Events\Event</c> 抽象基类的对应物（marker）。</summary>
public interface IEvent;

/// <summary>
/// 源项目 <c>app/Events/</c> 的 24 个事件。
///
/// <p>Laravel 用「传引用的数组」让监听者原地修改；.NET 没有引用传递，
/// 所以菜单/页头页脚/徽章事件持有<b>可变集合</b>，监听者直接改内容。</p>
/// </summary>
public static class Events
{
    // ------------------------------------------------------------ 菜单 / 路由

    public sealed record ConfigureAdminMenu(Dictionary<string, List<Dictionary<string, object?>>> Menu) : IEvent;

    public sealed record ConfigureExploreMenu(Dictionary<string, List<Dictionary<string, object?>>> Menu) : IEvent;

    public sealed record ConfigureUserMenu(Dictionary<string, List<Dictionary<string, object?>>> Menu) : IEvent;

    /// <summary><c>ConfigureRoutes</c>：<paramref name="Router"/> 是路由注册器（插件可加路由）。</summary>
    public sealed record ConfigureRoutes(object Router) : IEvent;

    // ------------------------------------------------------------ 玩家

    public sealed record PlayerProfileUpdated(PlayerRow Player) : IEvent;

    public sealed record PlayerRetrieved(PlayerRow Player) : IEvent;

    public sealed record PlayerWasAdded(PlayerRow Player) : IEvent;

    public sealed record PlayerWasDeleted(string PlayerName) : IEvent;

    public sealed record PlayerWillBeAdded(string PlayerName) : IEvent;

    public sealed record PlayerWillBeDeleted(PlayerRow Player) : IEvent;

    // ------------------------------------------------------------ 插件生命周期

    public sealed record PluginBootFailed(object Plugin) : IEvent;

    public sealed record PluginWasDeleted(object Plugin) : IEvent;

    public sealed record PluginWasDisabled(object Plugin) : IEvent;

    public sealed record PluginWasEnabled(object Plugin) : IEvent;

    // ------------------------------------------------------------ 渲染

    public sealed record RenderingBadges(List<Dictionary<string, object?>> Badges) : IEvent;

    public sealed record RenderingFooter(List<string> Contents) : IEvent
    {
        public void AddContent(string content) => Contents.Add(content);
    }

    public sealed record RenderingHeader(List<string> Contents) : IEvent
    {
        public void AddContent(string content) => Contents.Add(content);
    }

    // ------------------------------------------------------------ 材质

    public sealed record TextureDeleting(TextureRow Texture) : IEvent;

    // ------------------------------------------------------------ 用户

    public sealed record UserAuthenticated(UserRepository.UserRow User) : IEvent;

    public sealed record UserLoggedIn(UserRepository.UserRow User) : IEvent;

    public sealed record UserProfileUpdated(string Type, UserRepository.UserRow User) : IEvent;

    public sealed record UserRegistered(UserRepository.UserRow User) : IEvent;

    public sealed record UserTryToLogin(string Identification, string AuthType) : IEvent;
}
