using System.Globalization;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Storage;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\TextureController</c> 的移植（Minecraft 客户端契约部分）。
///
/// <p>已实现：<c>json</c>（<c>/{player}.json</c>、<c>/csl/{player}.json</c>）、
/// <c>texture</c>（<c>/textures/{hash}</c>、<c>/csl/textures/{hash}</c>）、
/// <c>raw</c>（<c>/raw/{tid}</c>），以及头像（2D/3D）、皮肤预览、披风预览
/// 六个图像端点（经 <c>Render.SkinRenderer</c> 等距渲染，PNG 输出）。</p>
/// </summary>
public sealed class TextureController(
    Option option,
    PlayerRepository players,
    TextureRepository textures,
    UserRepository users,
    string texturesDirectory)
{
    private readonly ITextureStorage _storage = new LocalTextureStorage(texturesDirectory);

    private readonly MinecraftRenderer _renderer = new();

    /// <summary>头像/预览的进程内缓存（<c>Cache::remember</c> 的等价物）。</summary>
    private readonly ImageCache _imageCache = new();

    /// <summary><c>GET {player}.json</c> / <c>GET csl/{player}.json</c>。</summary>
    public RequestHandler Json() => async (context, route, parameters) =>
    {
        string name = parameters.TryGetValue("player", out string? raw) ? raw ?? "" : "";
        PlayerRepository.PlayerDetail? player = players.FindDetailByName(name);
        if (player is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (player.Permission == UserRepository.Banned)
        {
            context.Response.StatusCode = 403;
            return;
        }

        TextureRow? skin = player.TidSkin > 0 ? textures.FindByTid(player.TidSkin) : null;
        TextureRow? cape = player.TidCape > 0 ? textures.FindByTid(player.TidCape) : null;

        var skins = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [skin?.TextureModel ?? "default"] = skin?.Hash,
        };
        var profile = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["username"] = player.Name,
            ["skins"] = skins,
            ["cape"] = cape?.Hash,
        };

        string payload = PhpJson.Encode(profile, PhpJson.JsonUnescapedUnicode);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = CacheControl();
        if (player.LastModified is not null)
        {
            context.Response.Headers["Last-Modified"] = HttpDate(player.LastModified);
        }

        await context.Response.WriteAsync(payload);
    };

    /// <summary><c>GET textures/{hash}</c> / <c>GET csl/textures/{hash}</c>。</summary>
    public RequestHandler Texture() => async (context, route, parameters) =>
    {
        string hash = parameters.TryGetValue("hash", out string? raw) ? raw ?? "" : "";
        await ServeTextureAsync(context, hash);
    };

    /// <summary><c>GET raw/{tid}</c>。</summary>
    public RequestHandler Raw() => async (context, route, parameters) =>
    {
        if (!RequestInput.ToBool(option.Get("allow_downloading_texture", true)))
        {
            context.Response.StatusCode = 403;
            return;
        }

        long tid = parameters.TryGetValue("tid", out string? raw) ? ToLong(raw) : 0;
        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        await ServeTextureAsync(context, texture.Hash);
    };

    // ================================================================== 头像

    /// <summary><c>avatar/player/{name}</c>：取玩家皮肤。</summary>
    public RequestHandler AvatarByPlayer() => async (context, route, parameters) =>
    {
        string name = parameters.TryGetValue("name", out string? raw) ? raw ?? "" : "";
        PlayerRepository.PlayerDetail? player = players.FindDetailByName(name);
        if (player is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        TextureRow? skin = player.TidSkin > 0 ? textures.FindByTid(player.TidSkin) : null;
        await AvatarAsync(context, skin);
    };

    /// <summary><c>avatar/user/{uid}</c>：取用户头像材质。</summary>
    public RequestHandler AvatarByUser() => async (context, route, parameters) =>
    {
        int uid = parameters.TryGetValue("uid", out string? raw) ? (int)ToLong(raw) : 0;
        UserRepository.UserRow? user = users.FindById(uid);
        TextureRow? texture = user is not null && user.Avatar > 0
            ? textures.FindByTid(user.Avatar)
            : null;
        await AvatarAsync(context, texture);
    };

    /// <summary><c>avatar/hash/{hash}</c>。</summary>
    public RequestHandler AvatarByHash() => async (context, route, parameters) =>
    {
        string hash = parameters.TryGetValue("hash", out string? raw) ? raw ?? "" : "";
        await AvatarAsync(context, textures.FindByHash(hash));
    };

    /// <summary><c>avatar/{tid}</c>。</summary>
    public RequestHandler AvatarByTexture() => async (context, route, parameters) =>
    {
        long tid = parameters.TryGetValue("tid", out string? raw) ? ToLong(raw) : 0;
        await AvatarAsync(context, textures.FindByTid(tid));
    };

    private async Task AvatarAsync(HttpContext context, TextureRow? texture)
    {
        if (texture is not null && texture.Type is not ("steve" or "alex"))
        {
            context.Response.StatusCode = 422;
            return;
        }

        int size = int.TryParse(context.Request.Query["size"], out int parsed) && parsed > 0
            ? parsed
            : 100;
        bool threeD = context.Request.Query.ContainsKey("3d");
        bool usePng = context.Request.Query.ContainsKey("png");
        string format = usePng ? "png" : "webp";
        string mode = threeD ? "3d" : "2d";
        TimeSpan ttl = option.GetBoolean("enable_avatar_cache", false)
            ? TimeSpan.FromDays(365)
            : TimeSpan.FromMinutes(1);

        if (texture is null || !_storage.Exists(texture.Hash))
        {
            byte[]? fallback = _renderer.FallbackAvatar(size, threeD);
            if (fallback is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            await WriteImageAsync(context,
                new ImageCache.Entry(Encode(fallback, usePng), ContentType(usePng), null));
            return;
        }

        string key = "avatar-" + mode + "-t" + texture.Tid + "-s" + size + "-" + format;
        ImageCache.Entry? cached = _imageCache.Get(key);
        if (cached is null)
        {
            byte[] skin = _storage.Read(texture.Hash);
            byte[]? png = threeD
                ? _renderer.Render3dAvatar(skin, size)
                : _renderer.Render2dAvatar(skin, size);
            if (png is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            string lastModified =
                HttpDate(DateTimeOffset.FromUnixTimeMilliseconds(
                    _storage.LastModifiedMillis(texture.Hash)));
            cached = new ImageCache.Entry(Encode(png, usePng), ContentType(usePng), lastModified)
            {
                ExpiresAt = DateTimeOffset.UtcNow + ttl,
            };
            _imageCache.Put(key, cached);
        }

        await WriteImageAsync(context, cached);
    }

    /// <summary><c>preview/hash/{hash}</c>。</summary>
    public RequestHandler PreviewByHash() => async (context, route, parameters) =>
    {
        string hash = parameters.TryGetValue("hash", out string? raw) ? raw ?? "" : "";
        await PreviewAsync(context, textures.FindByHash(hash));
    };

    /// <summary><c>preview/{texture}</c>。</summary>
    public RequestHandler Preview() => async (context, route, parameters) =>
    {
        long tid = parameters.TryGetValue("texture", out string? raw) ? ToLong(raw) : 0;
        await PreviewAsync(context, textures.FindByTid(tid));
    };

    private async Task PreviewAsync(HttpContext context, TextureRow? texture)
    {
        if (texture is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (!_storage.Exists(texture.Hash))
        {
            context.Response.StatusCode = 404;
            return;
        }

        bool usePng = context.Request.Query.ContainsKey("png");
        string format = usePng ? "png" : "webp";
        int height = int.TryParse(context.Request.Query["height"], out int parsed)
                     && parsed > 0
            ? parsed
            : 200;
        TimeSpan ttl = option.GetBoolean("enable_preview_cache", false)
            ? TimeSpan.FromDays(365)
            : TimeSpan.FromMinutes(1);

        // 源缓存键不含 height（Cache::remember('preview-t{id}-{format}')）。
        string key = "preview-t" + texture.Tid + "-" + format;
        ImageCache.Entry? cached = _imageCache.Get(key);
        if (cached is null)
        {
            byte[]? png;
            if (texture.Type == "cape")
            {
                png = _renderer.RenderCape(_storage.Read(texture.Hash), height);
            }
            else
            {
                // 皮肤预览：正反两面并排（renderSkin($skin, 12, $isAlex)）
                png = _renderer.RenderSkinPreview(_storage.Read(texture.Hash),
                    texture.Type == "alex");
            }

            if (png is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            string lastModified =
                HttpDate(DateTimeOffset.FromUnixTimeMilliseconds(
                    _storage.LastModifiedMillis(texture.Hash)));
            cached = new ImageCache.Entry(Encode(png, usePng), ContentType(usePng), lastModified)
            {
                ExpiresAt = DateTimeOffset.UtcNow + ttl,
            };
            _imageCache.Put(key, cached);
        }

        await WriteImageAsync(context, cached);
    }

    /// <summary>PNG/WebP 输出：<c>?png</c> 或缺省 webp 编码器时回退 PNG。</summary>
    private static byte[] Encode(byte[] png, bool usePng)
    {
        if (usePng)
        {
            return png;
        }

        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(png);
            using var output = new MemoryStream();
            image.Save(output, new WebpEncoder
            {
                FileFormat = WebpFileFormatType.Lossy,
                Quality = 100,
            });
            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidImageContentException or NotSupportedException)
        {
            return png;
        }
    }

    private static string ContentType(bool usePng) => usePng ? "image/png" : "image/webp";

    /// <summary>
    /// <c>cache.headers:etag;public;max_age=...</c> 的等价语义：写 Cache-Control/ETag，
    /// 条件命中时返回 304（不写 Content-Type/Content-Length）。
    /// </summary>
    private async Task WriteImageAsync(HttpContext context, ImageCache.Entry entry)
    {
        long maxAge = option.GetInt("cache_expire_time", 31536000);
        string? etag = EtagOf(entry.Body);
        bool notModified = false;

        string ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (etag is not null && ifNoneMatch.Length > 0)
        {
            string core = etag.Trim('"');
            notModified = ifNoneMatch.Split(',').Any(tag =>
            {
                string candidate = tag.Trim();
                if (candidate.StartsWith("W/", StringComparison.Ordinal))
                {
                    candidate = candidate[2..];
                }

                return candidate == "*" || candidate.Trim('"') == core;
            });
        }
        else if (etag is null && entry.LastModified is not null
                 && context.Request.Headers.IfModifiedSince.ToString() is { Length: > 0 } since
                 && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal, out DateTimeOffset sinceTime)
                 && DateTimeOffset.TryParse(entry.LastModified, CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal, out DateTimeOffset modifiedTime))
        {
            notModified = sinceTime >= modifiedTime;
        }

        context.Response.Headers["Cache-Control"] = "max-age=" + maxAge + ", public";
        if (etag is not null)
        {
            context.Response.Headers.ETag = etag;
        }

        if (notModified)
        {
            context.Response.StatusCode = 304;
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = entry.ContentType;
        context.Response.ContentLength = entry.Body.Length;
        if (entry.LastModified is not null)
        {
            context.Response.Headers["Last-Modified"] = entry.LastModified;
        }

        await context.Response.Body.WriteAsync(entry.Body);
    }

    /// <summary>Symfony <c>setEtag</c> 的摘要（Java 版同样使用 SHA-256）。</summary>
    private static string? EtagOf(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        return "\"" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(body)) + "\"";
    }

    private async Task ServeTextureAsync(HttpContext context, string hash)
    {
        if (!_storage.Exists(hash))
        {
            context.Response.StatusCode = 404;
            return;
        }

        long modifiedMillis = _storage.LastModifiedMillis(hash);
        string lastModified = HttpDate(DateTimeOffset.FromUnixTimeMilliseconds(modifiedMillis));
        string etag = "\"" + hash + "\"";

        // cache.headers:etag;public;max_age=... 的等价语义
        bool notModified = false;
        string ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch.Length > 0)
        {
            notModified = ifNoneMatch.Split(',').Any(tag =>
                tag.Trim().TrimStart('W', '/') == hash || tag.Trim() == etag);
        }
        else if (context.Request.Headers.IfModifiedSince.ToString() is { Length: > 0 } since
                 && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal, out DateTimeOffset sinceTime))
        {
            notModified = sinceTime.ToUnixTimeSeconds() >= modifiedMillis / 1000;
        }

        if (notModified)
        {
            context.Response.StatusCode = 304;
            context.Response.Headers["Cache-Control"] = CacheControl();
            context.Response.Headers.ETag = etag;
            return;
        }

        byte[] bytes = _storage.Read(hash);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers["Cache-Control"] = CacheControl();
        context.Response.Headers.ETag = etag;
        context.Response.Headers["Last-Modified"] = lastModified;
        await context.Response.Body.WriteAsync(bytes);
    }

    private string CacheControl() =>
        "max-age=" + option.GetInt("cache_expire_time", 31536000) + ", public";

    private string Timezone => "Asia/Shanghai";

    /// <summary>文件 mtime → HTTP 日期（UTC）。</summary>
    private static string HttpDate(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>（站点时区）→ HTTP 日期（UTC）。</summary>
    private string HttpDate(string value)
    {
        if (DateTime.TryParseExact(value.Length > 19 ? value[..19] : value,
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime local))
        {
            try
            {
                NodaTime.DateTimeZone zone = PhpDate.Zone(Timezone);
                // NodaTime：本地时间 → UTC
                var instant = zone.AtLeniently(
                    NodaTime.LocalDateTime.FromDateTime(local));
                return HttpDate(DateTimeOffset.FromUnixTimeMilliseconds(
                    instant.ToInstant().ToUnixTimeMilliseconds()));
            }
            catch (Exception)
            {
                return value;
            }
        }

        return value;
    }

    private static long ToLong(string? value) =>
        long.TryParse(value?.Trim(), out long parsed) ? parsed : 0;
}
