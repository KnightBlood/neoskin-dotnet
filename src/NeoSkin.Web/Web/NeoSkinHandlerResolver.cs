using NeoSkin.Web.Controllers;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Service.OAuth;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;

namespace NeoSkin.Web.Web;

/// <summary>action → 已移植的 handler（未移植的返回 null，由分发器回 501）。</summary>
public sealed class NeoSkinHandlerResolver(
    UserRepository users,
    PlayerRepository players,
    TextureRepository textures,
    ClosetRepository closet,
    ReportRepository reports,
    LanguageLineRepository languageLines,
    CommunicationRepository communications,
    NotificationRepository notifications,
    Database database,
    ICipher cipher,
    Option option,
    Config.NeoSkinSettings settings,
    OAuthService oauth,
    IMailer mailer,
    LoginFailCounter failures,
    string texturesDirectory,
    Paths.AppPaths paths) : IHandlerResolver
{
    private readonly AuthController _auth = new(users, players, cipher, option, settings, mailer, failures);

    private readonly PassportOAuthController _passportOAuth = new(oauth, null, cipher);

    private readonly PassportApiController _passportApi = new(oauth, null, cipher);

    private readonly PlayerController _player = new(option, players, textures, users, settings);

    private readonly TextureController _texture =
        new(option, players, textures, users, texturesDirectory);

    private readonly ClosetController _closet = new(option, closet, textures, users, settings);

    private readonly SkinlibController _skinlib =
        new(option, textures, users, players, closet, database, texturesDirectory, settings);

    private readonly ReportController _report =
        new(option, users, textures, players, closet, reports, database, texturesDirectory, settings);

    private readonly UserController _user =
        new(option, users, players, textures, cipher, mailer, settings);

    private readonly HomeController _home = new(option, users, settings);

    private readonly TranslationsController _translations = new(languageLines, settings);

    private readonly PlayersManagementController _playersAdmin =
        new(option, players, users, textures, settings);

    private readonly UsersManagementController _usersAdmin =
        new(option, users, cipher);

    private readonly ClosetManagementController _closetAdmin =
        new(users, textures, closet);

    private readonly NotificationsController _notifications =
        new(option, users, communications, notifications,
            new Service.Communications(communications, notifications, users, settings), settings);

    private readonly AdminController _admin = new(option, users, players, textures, settings);

    private readonly SetupController _setup =
        new(option, users, cipher, settings, paths, database);

    private readonly PluginController _plugins =
        new(new PluginManager(option, settings, paths), option, settings);

    private readonly MarketController _market =
        new(option, new PluginManager(option, settings, paths), settings);

    private readonly OptionsController _options = new(option, settings);

    private readonly SimpleViewController _simpleViews = new(option, users, settings);

    private readonly UpdateController _update = new(option, settings, paths);

    private readonly IgnitionController _ignition = new(settings);

    private static Service.Communications ConfigurePageViewProviders(
        CommunicationRepository communications, NotificationRepository notifications,
        UserRepository users, Config.NeoSkinSettings settings)
    {
        var service = new Service.Communications(communications, notifications, users, settings);
        PageView.InboxProvider = user => notifications.Unread(user.Uid, 20)
            .Where(service.Visible)
            .Select(row => (object?)new PhpArray()
                .With("id", row.Id)
                .With("title", service.Title(row)))
            .ToList();
        PageView.AnnouncementProvider = service.Announcements;
        return service;
    }

    private readonly CommunicationsController _communications =
        new(option, users, communications,
            ConfigurePageViewProviders(communications, notifications, users, settings),
            settings);

    public RequestHandler? Resolve(string action, string uri) => action switch
    {
        "App\\Http\\Controllers\\AuthController@login" => _auth.Login(),
        "App\\Http\\Controllers\\AuthController@register" => _auth.Register(),
        "App\\Http\\Controllers\\AuthController@forgot" => _auth.Forgot(),
        "App\\Http\\Controllers\\AuthController@reset" => _auth.Reset(),
        "App\\Http\\Controllers\\AuthController@verify" => _auth.Verify(),
        "App\\Http\\Controllers\\AuthController@handleLogin" => _auth.HandleLogin(),
        "App\\Http\\Controllers\\AuthController@logout" => _auth.Logout(),
        "App\\Http\\Controllers\\AuthController@handleRegister" => _auth.HandleRegister(),
        "App\\Http\\Controllers\\AuthController@handleForgot" => _auth.HandleForgot(),
        "App\\Http\\Controllers\\AuthController@handleReset" => _auth.HandleReset(),
        "App\\Http\\Controllers\\AuthController@handleVerify" => _auth.HandleVerify(),
        "App\\Http\\Controllers\\AuthController@captcha" => _auth.Captcha(),
        "App\\Http\\Controllers\\AuthController@fillEmail" => _auth.FillEmail(),

        "Laravel\\Passport\\Http\\Controllers\\AccessTokenController@issueToken" =>
            _passportOAuth.IssueToken(),
        "Laravel\\Passport\\Http\\Controllers\\AuthorizationController@authorize" =>
            _passportOAuth.Authorize(),
        "Laravel\\Passport\\Http\\Controllers\\ApproveAuthorizationController@approve" =>
            _passportOAuth.Approve(),
        "Laravel\\Passport\\Http\\Controllers\\DenyAuthorizationController@deny" =>
            _passportOAuth.Deny(),
        "Laravel\\Passport\\Http\\Controllers\\TransientTokenController@refresh" =>
            _passportOAuth.TransientRefresh(),

        "Laravel\\Passport\\Http\\Controllers\\ClientController@forUser" =>
            _passportApi.ClientsForUser(),
        "Laravel\\Passport\\Http\\Controllers\\ClientController@store" =>
            _passportApi.StoreClient(),
        "Laravel\\Passport\\Http\\Controllers\\ClientController@update" =>
            _passportApi.UpdateClient(),
        "Laravel\\Passport\\Http\\Controllers\\ClientController@destroy" =>
            _passportApi.DestroyClient(),
        "Laravel\\Passport\\Http\\Controllers\\AuthorizedAccessTokenController@forUser" =>
            _passportApi.TokensForUser(),
        "Laravel\\Passport\\Http\\Controllers\\AuthorizedAccessTokenController@destroy" =>
            _passportApi.DestroyToken(),
        "Laravel\\Passport\\Http\\Controllers\\PersonalAccessTokenController@forUser" =>
            _passportApi.PersonalTokensForUser(),
        "Laravel\\Passport\\Http\\Controllers\\PersonalAccessTokenController@store" =>
            _passportApi.StorePersonalToken(),
        "Laravel\\Passport\\Http\\Controllers\\PersonalAccessTokenController@destroy" =>
            _passportApi.DestroyPersonalToken(),
        "Laravel\\Passport\\Http\\Controllers\\ScopeController@all" =>
            _passportApi.Scopes(),

        "App\\Http\\Controllers\\PlayerController@index" => _player.Index(),
        "App\\Http\\Controllers\\PlayerController@list" => _player.List(),
        "App\\Http\\Controllers\\PlayerController@add" => _player.Add(),
        "App\\Http\\Controllers\\PlayerController@delete" => _player.Delete(),
        "App\\Http\\Controllers\\PlayerController@rename" => _player.Rename(),
        "App\\Http\\Controllers\\PlayerController@setTexture" => _player.SetTexture(),
        "App\\Http\\Controllers\\PlayerController@clearTexture" => _player.ClearTexture(),

        "App\\Http\\Controllers\\TextureController@json" => _texture.Json(),
        "App\\Http\\Controllers\\TextureController@texture" => _texture.Texture(),
        "App\\Http\\Controllers\\TextureController@raw" => _texture.Raw(),
        "App\\Http\\Controllers\\TextureController@avatarByPlayer" => _texture.AvatarByPlayer(),
        "App\\Http\\Controllers\\TextureController@avatarByUser" => _texture.AvatarByUser(),
        "App\\Http\\Controllers\\TextureController@avatarByHash" => _texture.AvatarByHash(),
        "App\\Http\\Controllers\\TextureController@avatarByTexture" => _texture.AvatarByTexture(),
        "App\\Http\\Controllers\\TextureController@preview" => _texture.Preview(),
        "App\\Http\\Controllers\\TextureController@previewByHash" => _texture.PreviewByHash(),

        "App\\Http\\Controllers\\ClosetController@index" => _closet.Index(),
        "App\\Http\\Controllers\\ClosetController@getClosetData" => _closet.GetClosetData(),
        "App\\Http\\Controllers\\ClosetController@allIds" => _closet.AllIds(),
        "App\\Http\\Controllers\\ClosetController@add" => _closet.Add(),
        "App\\Http\\Controllers\\ClosetController@rename" => _closet.Rename(),
        "App\\Http\\Controllers\\ClosetController@remove" => _closet.Remove(),

        "App\\Http\\Controllers\\UserController@index" => _user.Index(),
        "App\\Http\\Controllers\\UserController@profile" => _user.Profile(),
        "App\\Http\\Controllers\\UserController@user" => _user.User(),
        "App\\Http\\Controllers\\UserController@toggleDarkMode" => _user.ToggleDarkMode(),
        "App\\Http\\Controllers\\UserController@sendVerificationEmail" =>
            _user.SendVerificationEmail(),
        "App\\Http\\Controllers\\UserController@handleProfile" => _user.HandleProfile(),
        "App\\Http\\Controllers\\UserController@setAvatar" => _user.SetAvatar(),
        "App\\Http\\Controllers\\UserController@scoreInfo" => _user.ScoreInfo(),
        "App\\Http\\Controllers\\UserController@sign" => _user.Sign(),

        "App\\Http\\Controllers\\SkinlibController@library" => _skinlib.Library(),
        "App\\Http\\Controllers\\SkinlibController@handleUpload" => _skinlib.HandleUpload(),
        "App\\Http\\Controllers\\SkinlibController@show" => _skinlib.Show(),
        "App\\Http\\Controllers\\SkinlibController@upload" => _skinlib.Upload(),
        "App\\Http\\Controllers\\SkinlibController@info" => _skinlib.Info(),
        "App\\Http\\Controllers\\SkinlibController@delete" => _skinlib.Delete(),
        "App\\Http\\Controllers\\SkinlibController@privacy" => _skinlib.Privacy(),
        "App\\Http\\Controllers\\SkinlibController@rename" => _skinlib.Rename(),
        "App\\Http\\Controllers\\SkinlibController@type" => _skinlib.Type(),

        "App\\Http\\Controllers\\ReportController@submit" => _report.Submit(),
        "App\\Http\\Controllers\\ReportController@track" => _report.Track(),
        "App\\Http\\Controllers\\ReportController@manage" => _report.Manage(),
        "App\\Http\\Controllers\\ReportController@review" => _report.Review(),

        "App\\Http\\Controllers\\HomeController@apiRoot" => _home.ApiRoot(),
        "App\\Http\\Controllers\\HomeController@index" => _home.Index(),

        "App\\Http\\Controllers\\PluginController@getPluginData" => _plugins.GetPluginData(),
        "App\\Http\\Controllers\\PluginController@manage" => _plugins.Manage(),
        "App\\Http\\Controllers\\PluginController@readme" => _plugins.Readme(),
        "App\\Http\\Controllers\\PluginController@config" => _plugins.Config(),
        "App\\Http\\Controllers\\PluginController@upload" => _plugins.Upload(),
        "App\\Http\\Controllers\\PluginController@wget" => _plugins.Wget(),
        "App\\Http\\Controllers\\MarketController@marketData" => _market.MarketData(),
        "App\\Http\\Controllers\\MarketController@download" => _market.Download(),

        "App\\Http\\Controllers\\OptionsController@customize" => _options.Customize(),
        "App\\Http\\Controllers\\OptionsController@score" => _options.Score(),
        "App\\Http\\Controllers\\OptionsController@options" => _options.Options(),
        "App\\Http\\Controllers\\OptionsController@resource" => _options.Resource(),

        "App\\Http\\Controllers\\TranslationsController@list" => _translations.List(),
        "App\\Http\\Controllers\\TranslationsController@create" => _translations.Create(),
        "App\\Http\\Controllers\\TranslationsController@update" => _translations.Update(),
        "App\\Http\\Controllers\\TranslationsController@delete" => _translations.Delete(),

        "App\\Http\\Controllers\\PlayersManagementController@list" => _playersAdmin.List(),
        "App\\Http\\Controllers\\PlayersManagementController@name" => _playersAdmin.Name(),
        "App\\Http\\Controllers\\PlayersManagementController@owner" => _playersAdmin.Owner(),
        "App\\Http\\Controllers\\PlayersManagementController@texture" =>
            _playersAdmin.Texture(),
        "App\\Http\\Controllers\\PlayersManagementController@delete" => _playersAdmin.Delete(),

        "App\\Http\\Controllers\\UsersManagementController@list" => _usersAdmin.List(),
        "App\\Http\\Controllers\\UsersManagementController@email" => _usersAdmin.Email(),
        "App\\Http\\Controllers\\UsersManagementController@verification" =>
            _usersAdmin.Verification(),
        "App\\Http\\Controllers\\UsersManagementController@nickname" => _usersAdmin.Nickname(),
        "App\\Http\\Controllers\\UsersManagementController@password" => _usersAdmin.Password(),
        "App\\Http\\Controllers\\UsersManagementController@score" => _usersAdmin.Score(),
        "App\\Http\\Controllers\\UsersManagementController@permission" =>
            _usersAdmin.Permission(),
        "App\\Http\\Controllers\\UsersManagementController@delete" => _usersAdmin.Delete(),

        "App\\Http\\Controllers\\ClosetManagementController@list" => _closetAdmin.List(),
        "App\\Http\\Controllers\\ClosetManagementController@add" => _closetAdmin.Add(),
        "App\\Http\\Controllers\\ClosetManagementController@remove" => _closetAdmin.Remove(),

        "App\\Http\\Controllers\\NotificationsController@send" => _notifications.Send(),
        "App\\Http\\Controllers\\NotificationsController@all" => _notifications.All(),
        "App\\Http\\Controllers\\NotificationsController@read" => _notifications.Read(),

        "App\\Http\\Controllers\\AdminController@index" => _admin.Index(),
        "App\\Http\\Controllers\\AdminController@status" => _admin.Status(),
        "App\\Http\\Controllers\\AdminController@chartData" => _admin.ChartData(),

        "App\\Http\\Controllers\\UpdateController@showUpdatePage" => _update.ShowUpdatePage(),
        "App\\Http\\Controllers\\UpdateController@download" => _update.Download(),
        "App\\Http\\Controllers\\UpdateController@install" => _update.Install(),
        "App\\Http\\Controllers\\UpdateController@upload" => _update.Upload(),

        "App\\Http\\Controllers\\SetupController@database" => _setup.Database(),
        "App\\Http\\Controllers\\SetupController@finish" => _setup.Finish(),

        "Spatie\\LaravelIgnition\\Http\\Controllers\\HealthCheckController" =>
            _ignition.HealthCheck(),
        "Spatie\\LaravelIgnition\\Http\\Controllers\\ExecuteSolutionController" =>
            _ignition.ExecuteSolution(),
        "Spatie\\LaravelIgnition\\Http\\Controllers\\UpdateConfigController" =>
            _ignition.UpdateConfig(),

        "App\\Http\\Controllers\\CommunicationsController@index" => _communications.Index(),
        "App\\Http\\Controllers\\CommunicationsController@inbox" => _communications.Inbox(),
        "App\\Http\\Controllers\\CommunicationsController@message" => _communications.Message(),
        "App\\Http\\Controllers\\CommunicationsController@store" => _communications.Store(),
        "App\\Http\\Controllers\\CommunicationsController@update" => _communications.Update(),
        "App\\Http\\Controllers\\CommunicationsController@withdraw" => _communications.Withdraw(),
        "App\\Http\\Controllers\\CommunicationsController@preview" => _communications.Preview(),
        "App\\Http\\Controllers\\CommunicationsController@importSource" =>
            _communications.ImportSource(),

        // Route::view 的少量页面（setup / auth.bind）：手写布局，待模板引擎替换
        "Illuminate\\Routing\\ViewController" => _simpleViews.Resolve(uri),

        // Route::redirect('change-password', '/user/profile')
        "Illuminate\\Routing\\RedirectController" => Redirect("/user/profile"),

        _ => null,
    };

    /// <summary>固定目标的重定向路由（源 <c>Route::redirect</c>）。</summary>
    private static RequestHandler Redirect(string location) => (context, route, parameters) =>
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        return Task.CompletedTask;
    };
}
