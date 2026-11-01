# 数据库矩阵（本机 + CI）

> .NET 端命令与跨库坑位见本文件；工作区根目录的
> `..\DATABASE-MATRIX-PLAYBOOK.md` 是跨语言共享扩展版（不随仓库提交）。

本项目与 Java 版一样，用「同一套集成测试 + 5 个数据库」验证方言。测试入口是
`tests/NeoSkin.Tests/Db/DatabaseMatrixTest.cs`：只有 `DB_CONNECTION` 为
`mysql | mariadb | pgsql | sqlsrv` 时执行，否则 Skip。它会：

1. 用 `SchemaInstaller` 在目标库安装 17 张表（幂等）；
2. 插入/读取/删除一个用户；
3. 插入/读取/删除一个纹理（覆盖 `public` 小整数、`upload_at` 时间戳、自增主键）；
4. 校验 `FindJsonById` 的 `register_at` 输出为 PHP 的 `Y-m-d H:i:s`。

## 本机（无需安装服务）

`D:\WORKspace\NeoSkin\tools` 里已有免安装的数据库二进制与脚本：

```powershell
tools\scripts\db.cmd init all      # 首次（幂等）
tools\scripts\db.cmd start all
tools\scripts\db.cmd status
```

| 驱动 | 端口 | 账号 | 数据库 |
| --- | --- | --- | --- |
| mysql | 3307 | blessing / 123456 | blessing_skin |
| mariadb | 3308 | blessing / 123456 | blessing_skin |
| pgsql | 5433 | postgres / 123456 | blessing_skin |
| sqlsrv（WiltonDB TDS） | 1433 | wilton / 123456 | blessing_skin（测试自动建库） |

```powershell
# MySQL
$env:DB_CONNECTION='mysql'; $env:DB_HOST='127.0.0.1'; $env:DB_PORT='3307'
$env:DB_DATABASE='blessing_skin'; $env:DB_USERNAME='blessing'; $env:DB_PASSWORD='123456'
dotnet test NeoSkin.NET.slnx --filter "FullyQualifiedName~DatabaseMatrixTest"

# MariaDB 3308 / PostgreSQL 5433（postgres）同理
# WiltonDB（SQL Server 协议）
$env:DB_CONNECTION='sqlsrv'; $env:DB_HOST='127.0.0.1'; $env:DB_PORT='1433'
$env:DB_DATABASE='blessing_skin'; $env:DB_USERNAME='wilton'; $env:DB_PASSWORD='123456'
$env:DB_ENCRYPT='false'; $env:DB_TRUST_SERVER_CERTIFICATE='true'
dotnet test NeoSkin.NET.slnx --filter "FullyQualifiedName~DatabaseMatrixTest"
```

也可以只给 `DATABASE_URL`（覆盖 `DB_*`）：

```powershell
$env:DB_CONNECTION='mysql'
$env:DATABASE_URL='mysql://blessing:123456@127.0.0.1:3307/blessing_skin'
```

发布产物（AOT）冒烟：设置相同环境变量后启动 `publish/win-x64/neoskin.exe`，
请求 `/skinlib/list?filter=skin&page=1`、`/auth/login` 应返回 200。

## CI

`.github/workflows/ci.yml` 用与 Java 版相同的官方镜像（mysql:8 / mariadb:11 /
postgres:16 / mssql/server:2022）跑 5 个矩阵 job，另加 linux-x64 Native AOT 发布。

## 已知差异

- WiltonDB（Babelfish）不是真正的 SQL Server：过滤唯一索引、部分 T-SQL
  系统视图/优化器/锁语义不同；真 SQL Server 仍由 CI 的 `sqlsrv` job 覆盖。
- `DB_PREFIX` 目前只用于建表名，查询层尚未统一加前缀（与 Java 版相同）。

## 跨库易错点（.NET 实测）

- **SQLite 会掩盖问题**：重复 `Open()`、布尔写成 `1`、字符串写时间戳都能过；
  必须再用真实库跑矩阵。
- **连接工厂语义要统一**：SQLite 允许对已打开连接再次 `Open()`，MySQL/MariaDB 会抛
  `Cannot Open when State is Open`。
- **`boolean` ≠ `tinyInteger`**：本项目的 `users.verified` 是 `boolean`
  （PG `boolean` / SQL Server `BIT`），`textures.public` 是 `tinyInteger`
  （PG `smallint` / SQL Server `TINYINT`），写入参数分别是 bool 与 `0/1`。
- **PostgreSQL 字符串参数**：写入 timestamp / 比较 timestamp 的字符串参数要用
  `NpgsqlDbType.Unknown`（等价 JDBC `stringtype=unspecified`），否则报
  `expression is of type text`。
- **时间戳读取**：Npgsql 返回 `DateTime`，必须统一格式化为 `yyyy-MM-dd HH:mm:ss`。
- **整型宽度**：`INT` / `SERIAL` / `IDENTITY` 可能返回 `int`，统一用
  `Convert.ToInt64` 读取，避免 `GetInt64` 抛 `InvalidCastException`。
- **SQL Server 保留字**：`public` 是角色名/保留字，列名必须引用（`[public]`）。
- **SQL Server 分页**：必须 `ORDER BY … OFFSET n ROWS FETCH NEXT m ROWS ONLY`；
  单行用 `TOP 1`；自增回填用 `SCOPE_IDENTITY()`。
- **SQL Server 加密**：驱动默认 `Encrypt=true`；本地 WiltonDB 设
  `DB_ENCRYPT=false`、`DB_TRUST_SERVER_CERTIFICATE=true`。

