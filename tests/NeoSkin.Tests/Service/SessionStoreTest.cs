using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Service;

/// <summary><see cref="SessionStore"/> + <see cref="FileSessionHandler"/> 的移植测试。</summary>
public sealed class SessionStoreTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewSessionGetsIdAndToken()
    {
        var store = SessionStore.Begin("BS_SESSION", new FileSessionHandler(_dir, 120), null);
        store.Start();

        Assert.Equal(40, store.Id.Length);
        Assert.True(SessionStore.IsValidId(store.Id));
        Assert.True(store.Has(SessionStore.TokenKey));
        Assert.Equal(40, PhpString.Cast(store.Token()).Length);
    }

    [Fact]
    public void InvalidIdIsReplaced()
    {
        var handler = new FileSessionHandler(_dir, 120);
        Assert.NotEqual("bad", SessionStore.Begin("BS_SESSION", handler, "bad").Id);
        Assert.NotEqual(new string('a', 39), SessionStore.Begin("BS_SESSION", handler, new string('a', 39)).Id);
        Assert.Equal(new string('a', 40), SessionStore.Begin("BS_SESSION", handler, new string('a', 40)).Id);
    }

    [Fact]
    public void SaveAndReloadKeepsAttributesInPhpFormat()
    {
        var handler = new FileSessionHandler(_dir, 120);
        SessionStore store = SessionStore.Begin("BS_SESSION", handler, null);
        store.Start();
        store.Put("locale", "zh_CN").Put("score", 1000L);
        store.Save();

        string raw = File.ReadAllText(Path.Combine(_dir, store.Id));
        Assert.StartsWith("a:", raw); // PHP serialize 的数组
        Assert.Contains("s:6:\"locale\";", raw);

        SessionStore reloaded = SessionStore.Begin("BS_SESSION", handler, store.Id);
        reloaded.Start();
        Assert.Equal("zh_CN", reloaded.Get("locale"));
        Assert.Equal(1000L, reloaded.Get("score"));
        Assert.Equal(store.Token(), reloaded.Token());
    }

    [Fact]
    public void MissingOrExpiredFileLoadsEmptyWithFreshToken()
    {
        var handler = new FileSessionHandler(_dir, 120);
        SessionStore store = SessionStore.Begin("BS_SESSION", handler, new string('b', 40));
        store.Start();
        Assert.Equal(new string('b', 40), store.Id);
        Assert.True(store.Has(SessionStore.TokenKey));

        // 过期文件（mtime 超过 lifetime）读成空包
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, store.Id);
        File.WriteAllText(file, "a:1:{s:1:\"a\";i:1;}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-121));

        SessionStore expired = SessionStore.Begin("BS_SESSION", handler, store.Id);
        expired.Start();
        Assert.Null(expired.Get("a"));
    }

    [Fact]
    public void FlashDataAgesOnce()
    {
        var handler = new FileSessionHandler(_dir, 120);
        SessionStore store = SessionStore.Begin("BS_SESSION", handler, null);
        store.Start();
        store.Flash("msg", "saved");
        store.Save();

        SessionStore first = SessionStore.Begin("BS_SESSION", handler, store.Id);
        first.Start();
        Assert.Equal("saved", first.Get("msg"));
        first.Save();

        SessionStore second = SessionStore.Begin("BS_SESSION", handler, store.Id);
        second.Start();
        Assert.Null(second.Get("msg"));
    }

    [Fact]
    public void MigrateDestroysOldFileAndRotatesToken()
    {
        var handler = new FileSessionHandler(_dir, 120);
        SessionStore store = SessionStore.Begin("BS_SESSION", handler, null);
        store.Start();
        store.Put("uid", 1L);
        store.Save();

        string oldId = store.Id;
        string oldToken = PhpString.Cast(store.Token());
        store.Migrate(true);

        Assert.NotEqual(oldId, store.Id);
        Assert.False(File.Exists(Path.Combine(_dir, oldId)));
        Assert.NotEqual(oldToken, PhpString.Cast(store.Token()));
        Assert.Null(store.Get("uid"));
        Assert.True(store.Has(SessionStore.TokenKey));
    }

    [Fact]
    public void PreviousUrlAndGc()
    {
        var handler = new FileSessionHandler(_dir, 120);
        SessionStore store = SessionStore.Begin("BS_SESSION", handler, null);
        store.Start().SetPreviousUrl("https://example.com/a").Save();
        Assert.Equal("https://example.com/a", store.PreviousUrl());

        // gc 删除过期的 40 字符会话文件
        string expired = Path.Combine(_dir, new string('c', 40));
        File.WriteAllText(expired, "a:0:{}");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-3));
        Assert.True(handler.Gc(120 * 60) >= 1);
        Assert.False(File.Exists(expired));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }
}
