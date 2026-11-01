using Microsoft.Data.Sqlite;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Db;

/// <summary><see cref="UserRepository"/> 的 SQLite 集成测试。</summary>
public sealed class UserRepositoryTest : IDisposable
{
    private readonly string _file;
    private readonly NeoSkinSettings _settings;
    private readonly Database _db;
    private readonly UserRepository _users;

    public UserRepositoryTest()
    {
        _file = Path.Combine(Path.GetTempPath(), "neoskin-users-" + Guid.NewGuid().ToString("N") + ".sqlite");
        _settings = new NeoSkinSettings();
        _settings.Database.Connection = "sqlite";
        _settings.Database.Name = _file;
        _db = new Database(_settings);
        new SchemaInstaller(_db.Open, _settings).Install();
        _users = new UserRepository(_db);
    }

    [Fact]
    public void InsertAndFind()
    {
        int uid = _users.Insert("a@b.c", "Alice", 1000, "hash", "127.0.0.1", "Asia/Shanghai");
        Assert.True(uid > 0);

        UserRepository.UserRow? row = _users.FindByEmail("a@b.c");
        Assert.NotNull(row);
        Assert.Equal(uid, row!.Uid);
        Assert.Equal("Alice", row.Nickname);
        Assert.Equal(1000, row.Score);
        Assert.False(row.Verified);
        Assert.False(row.DarkMode);
        Assert.False(row.IsAdmin);
        Assert.Equal(1, _users.CountByEmail("a@b.c"));
        Assert.Equal(1, _users.CountByIp("127.0.0.1"));
        Assert.Equal(1, _users.CountAll());

        // last_sign_at 是「昨天」，register_at 是「现在」
        string? lastSign = _users.LastSignAt(uid);
        Assert.NotNull(lastSign);
        Assert.True(PhpDate.Parse(lastSign) < PhpDate.Parse(PhpDate.Now("Asia/Shanghai")));
    }

    [Fact]
    public void SuperAdminAndJsonView()
    {
        int uid = _users.InsertSuperAdmin("admin@a.c", "Boss", 1000, "hash", "127.0.0.1",
            "2026-10-26 00:00:00", "2026-10-26 00:00:00");
        UserRepository.UserRow? row = _users.FindById(uid);
        Assert.NotNull(row);
        Assert.True(row!.IsAdmin);
        Assert.True(row.Verified);
        Assert.Equal(UserRepository.SuperAdmin, row.Permission);

        Dictionary<string, object?>? json = _users.FindJsonById(uid);
        Assert.NotNull(json);
        Assert.False(json!.ContainsKey("password"));
        Assert.False(json.ContainsKey("remember_token"));
        Assert.Equal(uid, json["uid"]);
        Assert.Equal("Boss", json["nickname"]);
        Assert.Equal(false, json["is_dark_mode"]);
    }

    [Fact]
    public void Updates()
    {
        int uid = _users.Insert("u@a.c", "N", 0, "h", "ip", "Asia/Shanghai");

        _users.UpdateNickname(uid, "NewName");
        _users.UpdateEmail(uid, "n@a.c");
        _users.UpdateVerified(uid, true);
        _users.UpdateDarkMode(uid, true);
        _users.UpdateAvatar(uid, 7);
        _users.UpdateScoreAndSignAt(uid, 42, "2026-10-26 12:00:00");
        _users.UpdatePermission(uid, UserRepository.Admin);
        _users.ChangePassword(uid, "new-hash");

        UserRepository.UserRow row = _users.FindById(uid)!;
        Assert.Equal("NewName", row.Nickname);
        Assert.Equal("n@a.c", row.Email);
        Assert.True(row.Verified);
        Assert.True(row.DarkMode);
        Assert.Equal(7, row.Avatar);
        Assert.Equal(42, row.Score);
        Assert.True(row.IsAdmin);
        Assert.Equal("new-hash", row.Password);
        Assert.Equal("2026-10-26 12:00:00", _users.LastSignAt(uid));

        _users.UpdateEmailAndUnverify(uid, "again@a.c");
        Assert.False(_users.FindById(uid)!.Verified);
        _users.UpdateLocale(uid, "en");
        Assert.Equal("en", _users.FindById(uid)!.Locale);
    }

    [Fact]
    public void AudienceAndRegisterTimes()
    {
        _users.Insert("normal@a.c", "n", 0, "h", "ip", "Asia/Shanghai");
        _users.Insert("admin@a.c", "a", 0, "h", "ip", "Asia/Shanghai");
        int adminUid = _users.FindByEmail("admin@a.c")!.Uid;
        _users.UpdatePermission(adminUid, UserRepository.Admin);

        Assert.Equal(2, _users.IdsForAudience("all", null, null).Count);
        Assert.Single(_users.IdsForAudience("normal", null, null));
        Assert.Equal(adminUid, _users.IdsForAudience("uid", adminUid, null)[0]);
        Assert.Equal(adminUid, _users.IdsForAudience("email", null, "admin@a.c")[0]);

        Assert.Equal(2, _users.RegisterTimesSince("1970-01-01 00:00:00").Count);
        Assert.Empty(_users.RegisterTimesSince("2999-01-01 00:00:00"));
    }

    [Fact]
    public void InClosetAndDeleteWithPlayers()
    {
        int uid = _users.Insert("d@a.c", "d", 0, "h", "ip", "Asia/Shanghai");
        Assert.False(_users.InCloset(uid, 1));

        _db.Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u, 1, 'x')",
            ("@u", uid));
        Assert.True(_users.InCloset(uid, 1));

        _db.Execute("INSERT INTO players (uid, name, tid_cape, last_modified, tid_skin)"
            + " VALUES (@u, 'Steve', 0, '2026-10-26 00:00:00', -1)", ("@u", uid));
        Assert.Equal(1, _db.Long("SELECT COUNT(*) FROM players WHERE uid = @u", ("@u", uid)));

        _users.DeleteWithPlayers(uid);
        Assert.Null(_users.FindById(uid));
        Assert.Equal(0, _db.Long("SELECT COUNT(*) FROM players WHERE uid = @u", ("@u", uid)));
    }

    [Fact]
    public void MissingUserReturnsNull()
    {
        Assert.Null(_users.FindById(999));
        Assert.Null(_users.FindByEmail("nobody@a.c"));
        Assert.Null(_users.LastSignAt(999));
        Assert.Equal(0, _users.CountAll());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
