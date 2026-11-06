# NeoSkin.NET — 部署指南

> 最后更新：2026-11-06

## 1. 构建与发布

要求：.NET 10 SDK、C++ 构建工具（Native AOT）。

```sh
dotnet build NeoSkin.NET.slnx
dotnet test

# 自包含 Native AOT 发布（目标机无需 .NET 运行时）
dotnet publish src/NeoSkin.Web -c Release -r win-x64 -o publish/win-x64
# Linux: -r linux-x64
```

产物：`publish/<rid>/neoskin.exe`（Linux 为 `neoskin`），约 43 MB。
AOT 发布可能输出 `Microsoft.Data.SqlClient` / `ConfigurationManager` 的
IL2104/IL3053 裁剪警告，属上游库，不影响基本 SQL Server 访问。

## 2. 前端静态资源

仓库不提交前端产物。部署时把 PHP 源项目的 `public/app/` 复制到发布目录：

```powershell
Copy-Item ..\blessing-skin-server-Laravel13\public\app\* publish\win-x64\wwwroot\app\ -Recurse
```

`/lang/<locale>.js` 由后端按需生成。若使用 `--contentRoot`，静态资源放在
`<contentRoot>/wwwroot/app/`。

## 3. 配置（环境变量）

| 变量 | 说明 |
| --- | --- |
| `APP_KEY` | 必填，`base64:` + 32 字节密钥 |
| `APP_ENV` | `production` / `local` |
| `APP_DEBUG` | 必须为 `false` 才能使用本地前端资源 |
| `ASPNETCORE_URLS` | 监听地址，如 `http://0.0.0.0:5000` |
| `DB_CONNECTION` | `sqlite` / `mysql` / `mariadb` / `pgsql` / `sqlsrv` |
| `DB_HOST` / `DB_PORT` / `DB_DATABASE` / `DB_USERNAME` / `DB_PASSWORD` | 数据库连接 |
| `DATABASE_URL` | 可选，优先于 `DB_*` |
| `DB_ENCRYPT` / `DB_TRUST_SERVER_CERTIFICATE` | SQL Server TLS |
| `NEOSKIN_STORAGE_ROOT` | 存储根目录（默认 `./storage`） |
| `UPDATE_SOURCE` | 更新源 |

SQLite 示例：

```powershell
$env:APP_KEY='base64:...'
$env:APP_DEBUG='false'
$env:DB_CONNECTION='sqlite'
$env:DB_DATABASE='D:\neoskin\data\db.sqlite'
$env:NEOSKIN_STORAGE_ROOT='D:\neoskin\storage'
$env:ASPNETCORE_URLS='http://0.0.0.0:5000'
.\neoskin.exe
```

## 4. 安装与升级

新站点：

- 启动后访问 `/setup` 完成向导；或使用 CLI：

```powershell
.\neoskin.exe --neoskin.command=install --email=admin@example.com `
  --password=secret123 --nickname=Admin
```

升级：

```powershell
.\neoskin.exe --neoskin.command=update
```

请求期自动升级：已安装且 `options.version` 落后时，`RedirectToSetup` 调用
`UpdateRunner`（procedures → 版本号 → `SchemaInstaller` + `SchemaMigrator` →
`view:clear` → `install.lock` → 清缓存），并发锁串行化。

其它 CLI：`options:cache`、`plugin:enable|disable --name`、`salt:random [--show]`。

## 5. 从 PHP 迁移

1. 备份 PHP 站点与数据库。
2. 配置相同 `APP_KEY` 与数据库连接。
3. 复制 `public/app/`、`storage/textures/`。
4. 启动时会执行增量迁移与旧公告/站内信回填，不覆盖已有选项。
5. 验证登录、皮肤库、衣柜、管理端与 Minecraft `.json`。

## 6. 反向代理

```nginx
location / {
    proxy_pass http://127.0.0.1:5000;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    client_max_body_size 64m;
}
```

上传大小不小于站点选项 `max_upload_file_size`。健康检查：
`GET /__neoskin/health` → `ok`。

## 7. 常见问题

| 现象 | 处理 |
| --- | --- |
| 页面资源指向 `//localhost:8080` | `APP_DEBUG` 必须为 `false` |
| 启动后 302 `/setup` | 缺少 `storage/install.lock`；完成安装 |
| 升级后仍是旧版本 | 重启进程；AOT 二进制不能热替换 |
| SQL Server 连接失败 | 检查 `DB_ENCRYPT` / `DB_TRUST_SERVER_CERTIFICATE` |
| Linux 启动失败 | 确认发布 RID 与目标架构一致，且 AOT 依赖已安装 |
