# NeoSkin.NET 移植记录

> 只记录本项目的决策、偏差与踩坑；通用规则见 `AGENTS.md` / `MIGRATION.md`，
> 跨项目的 PHP 语义细节见仓库根目录 `../PORT-NOTES.md`。

---

## 1. 参考层级（重要）

| 层级 | 位置 | 地位 |
| --- | --- | --- |
| 1 | `../blessing-skin-server-Laravel13`（PHP） | **唯一权威**：路由、表结构、响应字节、模板 DOM、PHP 语义 |
| 2 | `../conformance/data/*.json` | PHP 现场生成的期望值，四语言共用，禁止手改 |
| 3 | `../NeoSkinSpringBoot`（Java） | 已验证的参考移植，可复用实现/HTML/契约结论；**冲突以 PHP 为准** |

之前 `MIGRATION.md` 里写的 EF Core / Razor / OpenIddict / `AssemblyLoadContext` 插件
在 Native AOT 下不可行，已按「AOT 优先」重写选型（§2）。

---

## 2. AOT 实测与选型

环境：.NET SDK `10.0.402`，Windows x64（MSVC 链接器可用）。

| 验证项 | 结果 |
| --- | --- |
| `dotnet new console` + `PublishAot=true` | ✅ 产出原生 exe |
| `dotnet new web`（minimal API）+ `PublishAot=true` | ✅ |
| 本项目 `dotnet publish -c Release -r win-x64` | ✅ `neoskin.exe` 9.7 MB |
| 原生二进制启动 + `GET /__neoskin/health` | ✅ 200 `ok` |

结论：AOT 本身没有阻塞项；限制集中在库的选择上：

- **不能用**：EF Core、运行时 Razor 编译、OpenIddict、反射式 JSON、
  `AssemblyLoadContext` 运行时加载插件 DLL。
- **改用**：ADO.NET + 手写方言；自研 Thymeleaf 子集渲染器 + 复用 Java 版 HTML 模板；
  自研 Passport 兼容 OAuth2；`System.Text.Json` source generator；
  插件走编译期注册（AOT 无 JIT，IL 插件程序集加载即失败）。
- `InvariantGlobalization` 保持 **false**：PHP 的 `mb_strtolower` / 排序需要完整
  Unicode 大小写映射，Invariant 模式只有 ASCII 语义。

---

## 3. Phase 0 交付

```
NeoSkin.NET.slnx
Directory.Build.props                     net10.0 / nullable / ICU
src/NeoSkin.Web/NeoSkin.Web.csproj        PublishAot / AssemblyName=neoskin
src/NeoSkin.Web/Program.cs                minimal API 骨架 + /__neoskin/health
src/NeoSkin.Web/Support/PhpJson.cs        PHP json_encode/json_decode 等价实现
tests/NeoSkin.Tests/Conformance/Corpus.cs 语料读取（缺失时用例 skip）
tests/NeoSkin.Tests/Support/PhpJsonTest.cs
```

测试：**16 passed / 0 failed**（其中 4 个是 conformance `json.json` 语料驱动）。

### 3.1 `PhpJson` 移植要点

- `PhpArray`：插入有序的关联数组（键归一化成 `long` 或 `string`），对应 Java 的
  `PhpJson.Array`（`LinkedHashMap`）。
- 浮点：`serialize_precision=-1` 的最短往返 + PHP 的定点/科学计数法切换规则
  （`decpt < -3 || decpt > 17`，科学计数法至少一位小数：`1.0e+20`）。
- 字符串：`/`→`\/`、非 ASCII→`\uXXXX`（小写；`JSON_HEX_*` 用大写）、`0x7F` 原样、
  `U+2028/2029` 无条件转义、孤立代理项报错。
- 解码：对象→`PhpArray`、空对象/空数组不可区分、重复键保留首次位置取最后值、
  超出 int64 的整数变 `double`。

### 3.2 踩坑：C# 三元表达式把 `long` 提升成 `double`

```csharp
// 错：long 与 double 的公共类型是 double，9223372036854775807 被转成 9.223372036854776E+18
return value.TryGetValue(out long l) ? l : value.GetValue<double>();
```

语料用例 `encode.int.max` 直接暴露。改为显式 `if` 分支返回 `long`。

---

## 4. Phase 1 完成：PHP 兼容层

| 模块 | 覆盖的 PHP 行为 | 测试 |
| --- | --- | --- |
| `Support/PhpJson.cs` | `json_encode`/`json_decode`（字节级、浮点最短往返、`\uXXXX`、`[]`/`{}`） | `json.json` 语料 71+9 条 + 边界 |
| `Support/PhpSerialize.cs` | `serialize()`（字节长度、不转义、`d:` 大写 E、自引用 `R:`） | `cookie.json` 13 条 + 冒烟 |
| `Support/PhpUnserialize.cs` | `unserialize()`（值/失败/错误 offset/O:/C:/E: 停点） | `cookie.json` 33 条 + 20 条边界 |
| `Support/PhpBase64.cs` | PHP 宽松/严格 `base64_decode`（24 组实测样本） | 手写 |
| `Support/PhpDate.cs` | Laravel `Y-m-d H:i:s` + 应用时区 | `datetime.json` 11 条 format 用例 |
| `Support/PhpString.cs` | `trim` 字符集、`mb_substr` 码点、`(string)` + `precision=14` | 手写（含 HALF_EVEN 舍入） |
| `Support/PhpArr.cs` | `Arr::get/has/exists/accessible/truthy` | 随后续模块补测 |
| `Support/PhpUrl.cs` | `rawurlencode`/`urlencode`/`urldecode`/`http_build_query` | 随后续模块补测 |
| `Support/SymfonyYaml.cs` | Symfony 标量规则（`yes` 是字符串、`007` 保持字符串、引号不判定） | 6 组（含真实语言包） |
| `Support/PhpVarExport.cs` | `options.php` 读 + 同格式写回 | 真实 PHP 生成样本 |

**81 tests / 0 failed**（Debug 与 Release 均验证过）。

### 4.1 新增依赖（均为 AOT 实测通过）

| 包 | 版本 | 用途 | AOT 验证 |
| --- | --- | --- | --- |
| NodaTime | 3.3.7 | IANA tzdata（历史偏移） | ✅ 原生探针输出 `1970-01-01 08:00:00` |
| YamlDotNet | 18.1.0 | YAML 节点解析（不用其类型推断） | ✅ 原生探针解析语言包成功 |

### 4.2 踩坑记录

1. **C# 三元表达式的类型提升（第二次踩）**：
   `return floating ? double.Parse(token) : long.Parse(token);` 的公共类型是 `double`，
   整数被静默转成浮点。`PhpVarExport` 的 `an_int` 因此变成 `Double`。
   统一改成显式 `if` 分支返回。
2. **Windows 时区库没有 IANA 历史偏移**：`Asia/Shanghai` 在 1901 年前是 LMT
   `+08:05:43`，`TimeZoneInfo` 只给 `+08:00`，与 PHP 差 343 秒
   （语料 `format.neg_1900`）。改用 NodaTime 的 tzdata。
3. **YamlDotNet 的 `TagName.Value` 对 non-specific tag 会抛**
   （`Cannot read the Value of a non-specific tag`）：必须先判断 `IsEmpty` /
   `IsNonSpecific`，再判断未知 tag；未知 `!!weird` 要按 Symfony 语义抛错。

## 5. Phase 2.1 完成：数据层骨架（Schema + 方言）

| 文件 | 内容 |
| --- | --- |
| `Config/NeoSkinSettings.cs` | 数据层需要的配置（connection/host/port/name/username/password/prefix/url + timezone/version） |
| `Db/SqlDialect.cs` | 5 种方言：类型映射、标识符引用、JSON 取值、分页、主键位置、MySQL 表后缀 |
| `Db/SchemaInstaller.cs` | 16 张业务表 + `migrations` 记账；幂等建表、`isInstalled`、`recordMigration`、`DdlStatements()` |

**88 tests / 0 failed**；SQLite 真实建表 + `PRAGMA table_info` 与语料 `final_tables` 对齐。

### 5.1 与 PHP 的对齐点（语料 `quirk.*`）

- 自增主键：`integer primary key autoincrement not null` / `int unsigned not null auto_increment primary key`
  / `serial not null primary key` / `int not null identity primary key`；
- 字符串主键：MySQL/SQLite 在 CREATE TABLE 末尾写 `primary key (...)`，
  PostgreSQL/SQL Server 拆成独立 `alter table … add primary key`；
- `uuid()`：SQLite `varchar`、MySQL `char(36)`、MariaDB/PG 原生 `uuid`、SQL Server `uniqueidentifier`；
- `tinyInteger()` 在 SQLite 落成 `integer`；`longText()` 在 PG/SQLite 退化成 `text`；
- MySQL 表后缀 `DEFAULT CHARACTER SET utf8mb4 COLLATE 'utf8mb4_unicode_ci'`，
  只有 `communications` 额外带 `ENGINE = InnoDB`；
- 无外键；`options.option_name` 无唯一索引。

### 5.2 已知偏差（同时记录在 MIGRATION.md §5.1）

1. **不回放迁移序列**：直接建最终结构，因此 `schema.json` 的 `ddl.*`（27 个 migration 的
   逐条 SQL，含 `alter table` 与 SQLite 临时表重建）不逐字比对；用 `final_tables` +
   `quirk.*` 证明表结构语义一致。
2. `users.locale` / `users.is_dark_mode`：只有 MySQL/MariaDB 按 `after` 插到中间，其余方言
   落在表尾——这是 PHP 的真实结果（SQLite 忽略 `after`），语料 `final_tables.users` 已体现。
3. 关键字大小写：本实现是 `UPPER`，PHP 是小写；比较时统一大小写并去掉 `varchar(n)` 长度。

### 5.3 AOT 验证

`Microsoft.Data.Sqlite 10.0.14` 在 Native AOT 下可用：原生探针实际建库
（`created=16`、二次安装 `0`、`migrations` 记账可查），4 种方言的 DDL 生成也正常。

## 6. Phase 2.2 完成：密码栈 + 数据访问入口

| 文件 | 内容 |
| --- | --- |
| `Service/Hash/PhpPasswordInfo.cs` | `password_get_info` / `password_needs_rehash` / `sscanf("%ld")` 式 cost 解析 / argon2 options 逐段扫描 |
| `Service/Hash/PhpPasswordVerify.cs` | `password_verify`：bcrypt（60 字节/前缀/cost 4..31）+ argon2i/argon2id（版本 19、无填充 base64） |
| `Service/Hash/PhpBcrypt.cs` | `$2y$` 生成、NUL→72 字节截断归一化 |
| `Service/Hash/LaravelHash.cs` | `Hash::make/check/isHashed/needsRehash`（`oauth_clients.secret` 用） |
| `Db/Database.cs` | 连接工厂 + 方言 + 参数化查询 + 自增主键（4 种方言的 identity 取法） |
| `Db/UserRepository.cs` | 登录/用户中心/管理端需要的读写方法 + `ReadJsonRow`（隐藏 password/remember_token） |

**103 tests / 0 failed**，其中 `hash.json` **219 条语料**全过（132 `get_info` + 54 `needs_rehash`
+ 24 `verify` + 9 `const`），SQLite 上的 `UserRepository` 集成测试 6 个。

### 6.1 与 PHP 的关键语义

- `password_get_info()` **只认 `$2y$` + 长度 60**：`$2a$`/`$2b$` 能通过 `check()` 但
  `isHashed()` 返回 false，`needsRehash()` 因此为 true（迁移脚本会重算）。
- cost 用 C 的 `sscanf(substr($hash, 4), "%ld")` 解析：跳过前导空白、可选符号、
  遇非数字停、读不到保持默认 12、溢出饱和、不要求后面有 `$`（`$2y$12!…` → 12）。
- argon2 options 逐段扫描（`v=` → `m=` → `,t=` → `,p=`），中途失败保留已生效的值。
- 密码归一化：第一个 NUL 字节截断 → 72 字节截断。

### 6.2 已知偏差

1. `$2x$` 在 .NET 侧按 `$2a$` 校验（BCrypt.Net-Next 不认 `2x` minor）。
   对 ASCII 密码二者结果相同；**非 ASCII + `$2x$`** 会与 PHP 不同（`2x` 有符号扩展 bug，
   PHP 7.4 之后不再产生这种哈希）。
2. Argon2 只支持版本 19（1.3）。版本 16（PHP 7.2 之前的产物）返回不匹配；
   真实站点只会产出 19。
3. Argon2 参数有防呆上界（1 GiB / time 1024 / p 255），避免被篡改的哈希串拖死进程。

## 7. Phase 3.1 完成：路由表与分发器

| 文件 | 内容 |
| --- | --- |
| `Resources/codegraph/routes.json` | 174 条（源 173 + 源码漏记的 `POST skinlib`） |
| `Web/Routes.cs` | `JsonDocument` 解析（无反射，AOT 安全）+ `RouteDefinition` |
| `Web/RouteTable.cs` | 路径/方法匹配、`Allow` 排序（Laravel `Router::$verbs`）、Laravel path 归一化、双斜杠判定 |
| `Web/RouteDispatcher.cs` | ASP.NET Core 终端处理器：匹配 → handler；否则 404/405/OPTIONS |
| `NeoSkinApp.cs` | 应用装配 + `route:list --json --columns=uri,name` 等价输出 |

**117 tests / 0 failed**。

### 7.1 与 Laravel 对齐的语义

- 路径不命中 → 404，消息 `The route X could not be found.`；
- 路径命中、方法不对 → 405 + `Allow: GET, HEAD, POST`（逗号**带**空格）；
- `OPTIONS` → 200 空体 + `Allow: GET,HEAD,POST`（逗号**不带**空格）+ `Cache-Control: no-cache, private`；
- 尾斜杠任意数量容忍；**前导/中间双斜杠** → 404（`/auth//login` 的消息是 `auth//login`）；
- `{player}.json` 这种「参数 + 字面后缀」用命名捕获组匹配；
- `QUERY` 方法按 GET 语义匹配（对应 Java 版的处理）。

### 7.2 原生二进制实测

```
GET /                → 501 {"code":501,"message":"该路由尚未移植"}
PUT /auth/login      → 405 Allow: GET, HEAD, POST
OPTIONS /auth/login  → 200 Allow: GET,HEAD,POST（空体）
GET /no-such         → 404 The route no-such could not be found.
GET /__neoskin/health→ 200 ok
```

### 7.3 中间件链与 cookie 加密（Phase 3.2）

| 文件 | 内容 |
| --- | --- |
| `Web/Middleware/MiddlewareChain.cs` | 全局栈 + `web/api/authorize` 组展开 + 别名改写（`role:admin` → `CheckRole:admin`） |
| `Service/Crypto/PhpEncrypter.cs` | Laravel 13 `Encrypter`：AES-128/256-CBC + GCM、`mac`（HMAC-SHA256，base64 串拼接）、`tag`、密钥轮换 |
| `Service/Crypto/CookieValuePrefix.cs` | `hash_hmac('sha1', name.'v2', key).'|'`（41 字节） |
| `Web/Cookie/EncryptCookies.cs` | 请求侧逐个解密 + 验前缀（失败置 null，不影响其他 cookie）、响应侧加前缀再加密 |

**131 tests / 0 failed**（`cookie.json` 的 constructor/encrypt/decrypt/appears/prefix/encryptcookies 全过）。

关键实现细节（都与 PHP 逐字节对齐）：

- MAC 是 `HMAC-SHA256(key, ivB64 + valueB64)`——两段都是 **base64 字符串**，不是原始字节；
- payload JSON **手动拼接**（`{"iv":…,"value":…,"mac":…,"tag":…}`），因为 base64 里会出现 `/`，
  用 `PhpJson` 会转义成 `\/` 而 PHP 是 `JSON_UNESCAPED_SLASHES`；
- CBC 的 `tag` 必须为空，GCM 的 `tag` 必须 16 字节，否则按 Laravel 的两种错误消息抛出；
- 解密失败顺序：payload 无效 → MAC 无效 → 解密失败；`unserialize` 的 warning 转成
  `ErrorException("unserialize(): …")`；
- 宽松 base64（payload 外层）会丢掉所有非字母数字字符并按模 4 补 `=`。

### 7.4 原生 AOT 实测

```
decrypt=zh_CN
roundtrip=zh_CN appears=True
prefix=8406079a850e53e30e4dcfb0dec36f08b84abe00|
cookie=8406079a850e53e30e4dcfb0dec36f08b84abe00|zh_CN
gcm=hello
```

### 7.5 会话与 CSRF（Phase 3.3）

| 文件 | 内容 |
| --- | --- |
| `Session/SessionHandler.cs` | `FileSessionHandler`（Laravel 文件驱动：过期判断、gc）+ cookie 配置 |
| `Session/SessionStore.cs` | `Store` 值层：id/token、PHP 序列化载荷、flash 轮转、`migrate()`、`_previous.url` |
| `Web/Middleware/SessionMiddleware.cs` | `EncryptCookiesMiddleware` + `StartSessionMiddleware` + `VerifyCsrfTokenMiddleware` |
| `Web/Middleware/PreventRequestForgery.cs` | 放行判据（reading / except / origin / token）与 `XSRF-TOKEN` cookie |

**142 tests / 0 failed**。

对齐要点：

- 会话 id = `[A-Za-z0-9]{40}`（非法 id 直接换新），`_token` 缺失时生成 40 位字母数字；
- 载荷用 **PHP serialize**（`a:…`），文件 mtime 超过 lifetime 视为空包；
- `save()` 先 `ageFlashData()`：`_flash.old` 的键被删、`old ← new`、`new ← []`；
- `migrate(true)` 删旧文件 + 换 id + 重新生成 token（登录防会话固定）；
- CSRF 放行顺序：HEAD/GET/OPTIONS → except（`*` 通配）→ `Sec-Fetch-Site: same-origin` →
  `_token` 字段 → `X-CSRF-TOKEN` → `X-XSRF-TOKEN`（先解密再去 41 字节前缀）；失败回 419；
- 响应补 `XSRF-TOKEN` cookie（不加密、非 HttpOnly、Max-Age=lifetime*60）。

HTTP 集成测试覆盖：GET 下发加密会话 cookie + XSRF cookie、无 token POST → 419、
`_token` 字段 / `X-CSRF-TOKEN` 头 → 200、会话 id 跨请求保持。

### 7.6 原生 AOT 实测

```
session id=40 locale=zh_CN token=40
csrf=True mismatch=False
```

### 7.7 语言层（Phase 3.4）

| 文件 | 内容 |
| --- | --- |
| `I18n/Locales.cs` | `config/locales.php` 的 8 键表、别名归一、`has/get/available/ids` |
| `Http/AcceptLanguage.cs` | Accept-Language 解析（q 值排序、去重）、`formatLocale`、`getPreferredLanguage` |
| `I18n/TranslationFileLoader.cs` | `Resources/lang/<locale>/<group>.yml`（Symfony YAML）、JSON 路径、命名空间、递归合并、路径安全检查 |
| `I18n/Translator.cs` | `get/has`、`parseKey`、占位符（`:name/:Name/:NAME`）、`strtr` 最长匹配 |
| `Web/Middleware/DetectLanguagePrefer.cs` | `lang` → cookie → `Accept-Language` → 别名 → fallback |

**159 tests / 0 failed**（`locale.json` 的 const / lookup / accept / resolve 全过）。

照抄的源站行为（不要「修」）：

- 三个来源都为空时落 **fallback_locale `en`**，不是 `app.locale zh_CN`
  （源码 `$locale ?? app()->getLocale();` 少了赋值）；
- `?lang=`（空串）会挡住 cookie 与 Accept-Language（`??` 只跳 null）；
- `zh-Hans-CN → zh_Hans_CN` 与别名键 `zh_HANS_CN` 大小写不同 → 落 fallback；
- `api` 路由不写 locale cookie，但解析照做；
- cookie 里存的是归一后的值（`en_US → en`）。

### 7.8 踩坑

- `PhpArr.Accessible` 原来只认 `PhpArray` / `IList`，**不认 `IDictionary`**，
  导致 `Translator` 查不到 `Dictionary<string, object?>` 里的键（返回原 key）。
  已补上 `IDictionary` 分支。

### 7.9 错误响应与中间件接线（Phase 3.5）

| 文件 | 内容 |
| --- | --- |
| `Error/PrettyJson.cs` | `json_encode($v, JSON_PRETTY_PRINT \| JSON_UNESCAPED_SLASHES)`（4 空格） |
| `Error/HttpStatus.cs` | Symfony `Response::$statusTexts` 子集 |
| `Error/ErrorPayload.cs` | `{message, exception:true, trace:[]}` |
| `Error/ContentNegotiation.cs` | `$request->expectsJson()`（ajax / pjax / `*/*` / 首个媒体类型含 json） |
| `Web/Middleware/MiddlewarePipeline.cs` | 按 `MiddlewareChain.Expand` 执行中间件；已接线 cookie / session / CSRF / 语言 |
| `Web/Cookie/CookieWriter.cs` | 手写 `Set-Cookie`（不做 URL 编码，与 Symfony 一致） |

**166 tests / 0 failed**；原生二进制实测：

```
GET /auth/login  → 501 + Set-Cookie: XSRF-TOKEN / BS_SESSION（加密） / locale（加密）
GET /nope (Accept: application/json)
                 → 404
                   {
                       "message": "The route nope could not be found.",
                       "exception": true,
                       "trace": []
                   }
POST /auth/login → 419 {"code":419,"message":"CSRF token mismatch."}
```

要点：

- 中间件对「未移植的 action」同样生效（501 是中间件链的末端），否则会话/CSRF
  在控制器接入前永远不会跑；
- `expectsJson()` 看 **Accept 排序后的第一项**：`text/html,application/json` 仍走 HTML；
- `Accept-Language` 与 `?lang=` 会被语言中间件解析并回写加密的 `locale` cookie；
- `api` 组没有 `web` 组，因此没有会话/CSRF；
- **HTML 错误页**（`errors.*` 模板）要等模板引擎（Phase 7）；当前非 JSON 请求
  返回一个最小 HTML 占位（标题与 Symfony 一致）。

## 8. Phase 4 前置：Option / AppPaths（Phase 4.1）

| 文件 | 内容 |
| --- | --- |
| `Service/OptionDefaults.cs` | `config/options.php` 的 **54** 个键（顺序敏感；不含 `max_texture_width`） |
| `Service/Option.cs` | `Option` 服务：缓存优先 → 数据库 → 默认值写回；`'true'/'('true')'` 类型转换；`option_localized` |
| `Paths/AppPaths.cs` | `base_path` / `storage_path` / `install.lock` / `options.php` / 纹理与插件目录 |

**173 tests / 0 failed**（`option.json` 的 default 54 / coerce 33 / get 3 / keys 2 / localized 12 全过）。

对齐要点：

- 缺键时用 `OptionDefaults` 并**写回数据库**；
- `'true'`/`'(true)'`/`'false'`/`'(false)'`/`'null'`/`'(null)'` 强制转换，`raw=true` 不转；
- 空值在内存里用哨兵表示（`ConcurrentHashMap` 不允许 null 的等价物），`get` 仍返回 null；
- `option_localized($key)` 先取基础键做默认值，再试 `$key.'_'.locale`；
- `storage/options.php` 缓存优先于数据库；坏缓存回退数据库（源站会抛错，这里更利于运维，
  已记录偏差）。

### 8.1 踩坑

- 测试里的 `items` 必须是**每个用例一个独立站点目录**：共用同一个 sqlite 会让上一个
  用例的 `copyright_prefer=3` 漏到 `neither_exists`（期望回退默认 `0`）。

## 9. 事件总线与密码驱动（Phase 4.2）

| 文件 | 内容 |
| --- | --- |
| `Event/Events.cs` | 24 个事件（菜单/路由、玩家、插件、渲染、材质、用户） |
| `Event/EventDispatcher.cs` | 类事件 + 字符串事件 + 「任意事件」监听；同步、按注册顺序 |
| `Db/Rows.cs` | 事件负载用的 `PlayerRow` / `TextureRow`（完整仓库随控制器补齐） |
| `Service/Cipher/Cipher.cs` | `BCRYPT` / `ARGON2I` / `PHP_PASSWORD_HASH` / `MD5` / `SALTED2*` / `SHA256` / `SHA512` |

**187 tests / 0 failed**（`cipher.json` 的 48 hash + 11 normalize + 4 reject + 9 roundtrip + 72 verify 全过）。

### 9.1 关键语义

- bcrypt 的 72 字节截断由 BCrypt.Net-Next 在 **UTF-8 字节**上完成，所以「截断点劈开多字节
  字符」的语料 `BCRYPT.normalize.splitMultibyte` 能对齐；自己按字符边界裁剪会算错。
- `password_hash()` 拒绝含 NUL 的密码（`Bcrypt password must not contain null character`），
  但 `crypt()` / `password_verify()` 在 NUL 处截断 —— 两条路径分开实现。
- `verify` 对三种「密码类」算法（BCRYPT / ARGON2I / PHP_PASSWORD_HASH）是同一份
  算法无关判定；摘要类走「重算 + 定长比较」。

## 10. 认证中间件与登录态（Phase 4.3）

| 文件 | 内容 |
| --- | --- |
| `Web/Auth/SessionGuard.cs` | `login_web_59ba36…` 会话键、`login()`（migrate(true)）、`logout()`、`user()` |
| `Web/Middleware/AuthMiddleware.cs` | `auth:web`（401/302）、封禁、邮箱补全跳转、`guest`、`role:*`、`verified` |
| `Service/LoginFailCounter.cs` | `sha1('login_fails_'.$ip)` 计数（进程内；缓存驱动后续接入） |
| `Web/Middleware/MiddlewarePipeline.cs` | 中间件支持**带参数**（`role:admin`、`auth:oauth`），并注册全部认证中间件 |

**198 tests / 0 failed**。

关键语义：

- 会话键必须逐字一致：`login_web_` + `sha1("Illuminate\Auth\SessionGuard")`
  = `login_web_59ba36addc2b2f9401580f014c7f58ea4e30989d`；
- `Auth::login()` 写键后 `migrate(true)`（换 id + 删旧文件 + 新 CSRF token），再重新写入；
- 未登录：JSON → **401 `{"message":"Unauthenticated."}`**；HTML → 302 `/auth/login`，
  并写 `last_requested_path` 与 `msg` 两个会话键；
- 封禁用户 JSON 返回 `{"code":-1,…}`（不是 1），且 `auth.logout` 路由放行；
- `role:*`：`permission < 阈值` → 403（message 为空）；未登录按 403；
- `verified`：只在 `require_verification` 打开时校验；
- `auth:oauth`（Passport）尚未接入 → 按未认证返回 401（下一步补）。

## 11. 校验器（Phase 4.4）

| 文件 | 内容 |
| --- | --- |
| `Web/Validation/Validator.cs` | Laravel `Validator` 子集：required/present/filled/email/string/integer/numeric/min/max/unique/in/regex/url/json/confirmed/nullable/array/boolean/exists/required_if + 自定义 `IRule` |
| 同上 | 通配符键展开（`blocks.*.type` → `blocks.0.type`）、`:attribute` / `:min` / `:values` 等占位符替换、语言包消息 |
| 同上 | `RfcEmailValidator`（`filter_var(FILTER_VALIDATE_EMAIL)` 的近似） |

**207 tests / 0 failed**。

要点：

- 隐式规则（required 等）失败后停止该字段后续校验；`unique`/`exists` 在前一条失败时跳过；
- `min`/`max` 对字符串按**码点**计长（`密码密码密码` = 6），对数值按数值比较；
- `regex` 是 PHP `preg_match` 的「搜索」语义（不是整段匹配）；
- 未声明规则的字段不参与「缺失即跳过」以外的校验；`present` 只看键是否存在；
- 同一个 Validator 实例失败后不可复用（与 Laravel 一致），`Validated()` 只返回声明过规则的字段。

## 12. AuthController 全流程（Phase 4.5）

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/AuthController.cs` | `handleLogin` / `logout` / `handleRegister` / `handleForgot` / `handleReset` / `handleVerify` / `captcha` / `fillEmail` |
| `Support/SignedUrl.cs` | Laravel `signedRoute` / `temporarySignedRoute` / `hasValidSignature`（HMAC-SHA256，参数 ksort） |
| `Db/DbPresenceVerifier.cs` | `unique` / `exists` 规则的数据库存在性查询 |
| `Service/Mailer.cs` | `IMailer`：smtp / log / array 三种 mailer（对应源 `config/mail.php`） |
| `Web/Validation/Rules.cs` | `CaptchaRule`（reCAPTCHA 或会话验证码）与 `PlayerNameRule` |

**225 tests / 0 failed；Native AOT publish 通过（21.8 MB）**。

要点与偏差：

- 会话验证码 `captcha()`：短语仍是 5 位（字符集与源一致），但图片改用 **ImageSharp** 生成
  JPEG（源/Java 用 java.awt）。图片**字节不保证与 PHP Imagick 一致**，但 Content-Type、
  `Cache-Control: no-store`、尺寸（100×34）与会话写入语义一致。
- 签名 URL 的 HMAC key 是 `config('app.key')` **原串**（含 `base64:` 前缀），
  与 Java 版一致；签名字符串为 `path?按 key 排序的查询串`（与 Java 的 TreeMap 对齐）。
- 邮箱验证链接是**非临时签名**（无 `expires`）；重置密码链接是临时签名（1 小时）。
- `handleForgot` 成功分支会真实发信；测试只覆盖不碰网络的失败分支（未知邮箱 / 校验失败），
  与 Java 版测试策略一致。
- `can_register` / `can_login` / `client_ip` 钩子（`BlessingFilter`）已接线：
  `AuthController` 的注册/登录入口与 `ClientIp()`，见 §52。
- HTML 视图（login/register/forgot/reset/verify/bind）仍等 Phase 7 模板引擎。
- 签名无效时走 `abort()` 语义：JSON 请求返回 `{message, exception:true, trace:[]}`，
  其余返回状态页（与源 `Handler::render()` 一致，见 `ErrorPayload`）。

## 13. OAuth2 / Passport（Phase 4.6）

| 文件 | 内容 |
| --- | --- |
| `Service/OAuth/OAuthKeys.cs` | `storage/oauth-*.key`：PKCS#1 读写（兼容 PHP/OpenSSL），也接受 PKCS#8 / X.509 |
| `Service/OAuth/Jwt.cs` | RS256（access token）与 HS256（transient cookie）编解码 |
| `Service/OAuth/OAuthStore.cs` | `oauth_clients/auth_codes/access_tokens/refresh_tokens` 的 ADO.NET 访问 |
| `Service/OAuth/OAuthService.cs` | 作用域表、令牌签发、Bearer / `laravel_token` 认证、transient cookie |
| `Web/Controllers/PassportOAuthController.cs` | `/oauth/token`（authorization_code / refresh_token / client_credentials）、`/oauth/authorize`（approve / deny）、`/oauth/token/refresh` |
| `Web/Controllers/PassportApiController.cs` | `oauth/clients`、`oauth/tokens`、`oauth/personal-access-tokens`、`oauth/scopes` |
| `Web/Middleware/AuthMiddleware.cs` | `auth:oauth`（Bearer/cookie）+ `scope:*` / `scopes:*`（401 / 403） |
| `Web/Middleware/MiddlewarePipeline.cs` | `CreateFreshApiToken`（给已登录 web 请求下发 `laravel_token`） |

**238 tests / 0 failed；Native AOT publish 通过（22 MB）**。

要点与偏差：

- access token 是 RS256 JWT（`aud/jti/iat/nbf/exp/sub/scopes`），与 league/oauth2-server v9 同构；
  `expires_in` 固定 `31536000`（P1Y）。
- 密钥首次使用时生成 **PKCS#1**（`BEGIN RSA PRIVATE KEY` / `BEGIN RSA PUBLIC KEY`），
  与 PHP 的 `passport:keys` 输出格式一致；已有的 PHP 密钥可直接读取。
- refresh token 是 80 位十六进制明文（源为加密载荷），与 Java 版偏差一致。
- `/oauth/authorize` 在「已授权相同 scope」时直接签发 code 并 302；否则返回**最小可用的
  授权表单**（字段 `_token` / `auth_token`），Phase 7 会换成 `oauth/authorize` 模板。
- `scope` 参数按源站拼写保留 `Closet.ReadWrtie`（历史错字，前端契约）。
- `CreateFreshApiToken` 只在会话已登录时下发 cookie；transient cookie 的 CSRF 校验读
  `X-CSRF-TOKEN` / `X-XSRF-TOKEN`。

## 14. Phase 5.1：玩家管理与 Minecraft 纹理契约

| 文件 | 内容 |
| --- | --- |
| `Db/PlayerRepository.cs` | 补齐 `FindDetailByName` / `ListByUid` / `CountByUser` / `CountAll` / `Delete` / `UpdateName` / `UpdateTextures` / `UpdateOwner` / `LastModified` |
| `Db/TextureRepository.cs` | `textures` 表访问（find/insert/delete/update + 统计） |
| `Storage/TextureStorage.cs` | `ITextureStorage` + 本地盘 `storage/textures/<hash>` |
| `Web/Input/RequestInput.cs` | query + form/JSON 合并输入（源 `RequestInput` 中间件等价物） |
| `Web/Controllers/PlayerController.cs` | `list / add / delete / rename / setTexture / clearTexture`（JSON） |
| `Web/Controllers/TextureController.cs` | `json`（`/{player}.json`、`/csl/{player}.json`）、`texture`、`raw` |

**251 tests / 0 failed；Native AOT publish 通过（22.1 MB）**。

要点与偏差：

- 玩家 JSON 键序与 `Player::toArray()` 一致：`pid, uid, name, tid_cape, last_modified, tid_skin`。
- 添加/删除玩家按 `score_per_player`（默认 100）扣/返积分，`return_score` 控制是否返还；
  事件顺序：`PlayerWillBeAdded` → 写库 → `PlayerWasAdded`；删除为 `PlayerWillBeDeleted` → 删除 → `PlayerWasDeleted`。
- `/{player}.json`：`skins` 的键是 `Texture::getModelAttribute()`（`alex` → `slim`），
  披风为 `cape`；封禁用户 403、不存在 404；`Cache-Control: max-age=…, public` + `Last-Modified`。
- `/textures/{hash}` 支持 `ETag` / `If-None-Match` / `If-Modified-Since`（304）。
  **偏差**：ASP.NET 会把 `Cache-Control` 指令重排为 `public, max-age=…`（语义等价）。
- `Storage.Textures` 默认值修正为 `storage/textures`（与 PHP/Java 一致）。
- 头像/预览（`avatar/*`、`preview/*`）与 `player/index` HTML 视图仍待图像渲染器与模板引擎。

## 15. Phase 5.2：用户中心 / 衣柜 / 皮肤库

| 文件 | 内容 |
| --- | --- |
| `Db/ClosetRepository.cs` | `user_closet` 关联表：分页/计数/allIds/attach/detach/rename + `TextureJson` |
| `Web/Pagination/Paginator.cs` | `LengthAwarePaginator::links()` + `UrlWindow`（JSON 分页链接） |
| `Web/Controllers/UserController.cs` | `user` / `scoreInfo` / `sign` / `toggleDarkMode` / `handleProfile` / `setAvatar` / `sendVerificationEmail` |
| `Web/Controllers/ClosetController.cs` | `getClosetData` / `allIds` / `add` / `rename` / `remove` |
| `Web/Controllers/SkinlibController.cs` | `library` / `info` / `delete` / `privacy` / `rename` / `type` |
| `Service/TextureSideEffects.cs` | `texture.deleted` / `texture.privacy.updated` 的三个监听器（退分、重置角色、清理衣柜） |

**277 tests / 0 failed；Native AOT publish 通过（22.3 MB）**。

要点与偏差：

- 分页 JSON 与源 `LengthAwarePaginator::links()` 对齐（省略处 `label="..."`、`url=null`），
  `UrlWindow` 的 `onEachSide` 默认 3。
- `scoreInfo` 的 `rate.storage` 用 PHP `(int) option('score_per_storage')` 语义（`true` → `1`）。
- 签到：`sign_after_zero` / `sign_gap_time` 语义对齐；间隔内重复签到返回 `{code:1,message:""}`。
- 修改密码/邮箱/删除账号都会 `Auth::logout()`；删除账号调 `users.delete()`（先删角色再删用户）。
- `skinlib/list` / `skinlib/info` 的路由只有 `web` 组，不经过 `auth:web`，
  因此控制器在读当前用户时回退到会话（源同样直接 `Auth::user()`）。
- 隐私切换的 `ResetPlayers` 与源一致：**排除上传者本人**（`uid <> uploader`）。
- `CreateFreshApiToken` 修复：cookie 的 `csrf` 取自 handler 执行后的会话 token
  （登录会 migrate，请求开始时捕获的旧值会导致 `/api/user` 401）。
- `skinlib/show` / `skinlib/upload` / `texture`（上传）/ `show` 仍待模板与图片处理；
  `ReportController`、管理端控制器未开始。

## 16. Phase 5.3：举报（ReportController）

| 文件 | 内容 |
| --- | --- |
| `Db/ReportRepository.cs` | 举报行访问：`FindById` / `CountByReporterAndTid` / `Insert` / `UpdateStatus` / `PageByReporter` / `AdminPage` |
| `Support/SearchString.cs` | `App\Support\SearchString` 的实用子集：列条件、`IN`、`sort/order_by/fields` 关键字；非法时「全部结果」 |
| `Web/Controllers/ReportController.cs` | `submit` / `manage` / `review`（`track` HTML 待模板） |

**284 tests / 0 failed；Native AOT publish 通过（22.4 MB）**。

要点与偏差：

- `manage` 的行内嵌 `texture` / `texture_uploader` / `informer`（缺失为 `null`），
  分页字段与源一致（`current_page/data/from/last_page/per_page/to/total`，**无 links**）。
- `review` 支持 `reject` / `delete` / `ban`；`delete` 走 `TextureSideEffects.onDeleted`；
  材质已不存在时退还举报分并直接 `resolved`（`general.texture-deleted`）。
- `ban` 要求操作者权限严格高于上传者，否则 `{code:1}`。
- **SearchString 为子集**：未实现括号 / `or` / `not` / 关系列 / 日期边界扩展；
  无法解析时按源的 `all-results` 处理。已记录，管理端常见查询均可用。
- `texture` 上传（`handleUpload`）仍待图片消毒管线（Imagick → ImageSharp 的字节级等价）。

## 17. Phase 5.4：`GET /api` 与固定重定向

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/HomeController.cs` | `apiRoot`：`blessing_skin` / `spec` / `copyright` / `site_name` |
| `Web/NeoSkinHandlerResolver.cs` | `Route::redirect` 的固定 302（`.well-known/change-password` → `/user/profile`） |

**286 tests / 0 failed；AOT publish 22.4 MB**。

要点：

- `copyright_prefer` 是 `COPYRIGHTS` 数组下标；越界/非数字返回 `null`。
- `Cache-Control: no-cache, private` 与源一致。
- 首页 `home` 与 `ViewController` 页面仍等模板引擎（Phase 7）。

## 18. Phase 5.5：纹理上传（PNG 消毒）

| 文件 | 内容 |
| --- | --- |
| `Support/PngEncoder.cs` | 与 Imagick 参数对齐的 PNG 编码器：IHDR/IDAT/IEND、RGBA、libpng 行过滤启发式、zlib level 9、IDAT 8192 分块 |
| `Service/PngSanitizer.cs` | `IsPng` / `ReadSize`（IHDR）/ `Sanitize`（ImageSharp 解码 → 重编码） |
| `Web/Controllers/SkinlibController.cs` | `handleUpload`：文件校验、尺寸/比例校验、去重、积分、落库、写盘、加入衣柜 |

**292 tests / 0 failed；AOT publish 24 MB**。

要点：

- 哈希基于**消毒后的 PNG 字节**（与 PHP 一致）；同 hash 已存在时不再写文件。
- 尺寸规则：宽高是 64/32 的倍数；皮肤宽高比 ∈ {2,1}，`alex` 不能是 2；披风比必须为 2；
  宽 > `max_texture_width` 时报 `too-wide`。
- 积分：`ceil(字节/1024) × (公开 ? score_per_storage : private_score_per_storage)`
  `+ score_per_closet_item − score_award_per_texture`。
- 上传成功自动 `attach` 到衣柜，返回 `{code:0, data:{tid}}`。
- **偏差**：PNG 重编码用 .NET zlib（level 9）；扫描线过滤算法与 libpng 一致，
  但压缩字节不保证与 PHP Imagick 完全相同（Java 版同样记录该偏差）。
- `file` 规则做成隐式（Laravel 里 `required|mimes|max` 对缺失文件同样报错）。

## 19. Phase 5.6：2D 头像端点

| 文件 | 内容 |
| --- | --- |
| `Service/MinecraftRenderer.cs` | `Render2dAvatar`（头部正面 8×8 + 帽子层 alpha 合成 + 缩放）与 `FallbackAvatar` |
| `Resources/misc/textures/avatar{2d,3d}.png` | 回退头像（从 Java 版资源复制，源与 PHP 一致） |
| `Web/Controllers/TextureController.cs` | `avatarByTexture` / `avatarByHash` / `avatarByPlayer` / `avatarByUser` |

**296 tests / 0 failed；AOT publish 24 MB（含 Resources/misc）。**

要点与偏差：

- `avatar/{tid}`、`avatar/hash/{hash}`、`avatar/player/{name}`、`avatar/user/{uid}`
  均输出 `image/png`，`?size=` 默认 100；披风 → 422；材质缺失 → 回退图。
- **`?3d` 暂按 2D 渲染**（3D 等距投影渲染器未移植）；回退图会按 `3d` 选
  `avatar3d.png`。
- 缩放用最近邻取样；PHP 的 `resize()` 是双线性重采样，**头像像素不保证逐字节一致**。
- 皮肤/披风预览（`preview/*`）仍为 501。

## 20. Phase 5.7：管理端翻译（admin/i18n）

| 文件 | 内容 |
| --- | --- |
| `Db/LanguageLineRepository.cs` | `language_lines` 读写：`Find` / `List`（每页 10）/ `Save` / `Delete`（`text` 为保持顺序的 JSON 对象） |
| `Web/Controllers/TranslationsController.cs` | `list` / `create` / `update` / `delete` |

**302 tests / 0 failed；AOT publish 24.1 MB。**

要点：

- `list` 返回 `current_page/data/from/last_page/per_page/to/total`；`text` 是
  locale → 文案的对象（与 Eloquent 的 `array` cast 一致）。
- `create` 成功 302 到 `/admin/i18n` 并写会话 `success`；校验失败时 JSON → 422、
  表单 → 302（Referer 或 `/admin/i18n`）。
- `update` 按 `array_merge` 语义覆盖当前 locale；`delete` 返回 `{code:0}`。
- `text` 列按 PHP `json_encode` 默认转义非 ASCII（`\uXXXX`）；读取时解码。
- 前端语言包缓存失效（`JavaScript.resetTime`）未实现——当前无缓存层，语义等价。

## 21. Phase 6.1：管理端玩家管理

| 文件 | 内容 |
| --- | --- |
| `Db/PlayerRepository.cs` | `AdminPage`（原始列 + 分页） |
| `Support/SearchString.cs` | 新增列别名（`skin`/`cape`）与「纯词 → 可搜索列 LIKE」 |
| `Web/Controllers/PlayersManagementController.cs` | `list` / `name` / `owner` / `texture` / `delete` |

**312 tests / 0 failed；AOT publish 24.1 MB。**

要点：

- `list` 返回 `current_page/data/from/last_page/per_page/to/total`，`data` 是玩家原始列
  （`pid/uid/name/tid_skin/tid_cape/last_modified`）。
- 权限守卫：角色拥有者不是当前管理员且权限 **≥** 当前管理员 → 403
  （`{code:1, message: admin.players.no-permission}`）；找不到玩家 404。
- `name` 校验：`required` + `PlayerNameRule` + 长度 + `unique:players,name`。
- `texture` 允许 `tid=0`（清除）；非 0 且不存在 → `{code:1}`。
- `owner` 目标用户不存在 → `{code:1}`。

## 22. Phase 6.2：管理端用户与衣柜

| 文件 | 内容 |
| --- | --- |
| `Db/UserRepository.cs` | `AdminPage`（原始列 + 分页，布尔/整数归一化） |
| `Web/Controllers/UsersManagementController.cs` | `list` / `email` / `verification` / `nickname` / `password` / `score` / `permission` / `delete` |
| `Web/Controllers/ClosetManagementController.cs` | `list`（`api/admin/closet`）/ `add` / `remove`（`admin/closet`） |

**327 tests / 0 failed；AOT publish 24.2 MB。**

要点：

- 用户列表支持 `email`/`nickname` 全文搜索与 `permission:0` 等条件；`data` 为原始列
  （`uid/email/nickname/.../verified/is_dark_mode`）。
- 守卫：目标用户权限 ≥ 当前管理员且不是自己 → 403；不存在 → 404。
- `email` 唯一校验排除自身（`unique:users,email,{uid},uid`）；密码规则 `min:8|max:16`。
- `permission` 仅允许 `-1/0/1`；授予 `admin` 需要超级管理员；不能修改自己的权限。
- 删除用户走 `DeleteWithPlayers`（先删角色）。
- 管理端衣柜：`add` 用材质的 `name` 作为 `item_name`；`remove` 返回
  `{user, texture}`；列表含 `pivot` 且**不含** `model`（与源 `toArray()` 一致）。

## 23. Phase 6.3：站内信（NotificationsController）

| 文件 | 内容 |
| --- | --- |
| `Db/CommunicationRepository.cs` | `communications` 读写（插入 / find / title / `ExistsVisible`） |
| `Db/NotificationRepository.cs` | `notifications` 读写（投递 / `Unread` / `FindByNotifiableAndId` / `MarkAsRead`） |
| `Service/Communications.cs` | 通知的 `Title` / `Content` / `Visible` 读取子集 |
| `Web/Controllers/NotificationsController.cs` | `send`（302 `/admin`）/ `all`（未读）/ `read` |

**332 tests / 0 failed；AOT publish 24.2 MB。**

要点与偏差：

- `send` 先写 `communications`（`kind=message`、`status=published`、`editor=source`、
  `audience` 为 receiver/uid/email 的 JSON），再按收件人集合逐条写 `notifications`；
  `type=App\Notifications\SiteMessage`、`notifiable_type=App\Models\User`、UUIDv4 带连字符。
- 收件人：`all` / `normal` / `uid` / `email`（`users.idsForAudience`）。
- `all` 返回未读（上限 1000）的 `{id,title}`；`read` 校验 `read_at IS NULL` 与可见性，
  成功后返回 `{title,content,time}`，再次读取 → 404。
- 未读查询用方言的 JSON 提取（SQLite `JSON_EXTRACT`）比对 `communication_id` /
  `delivery_token`，把已撤回/重新投递的旧通知挡掉。
- **偏差**：块编辑器（`blocks`）渲染器未移植；`content` 直接返回 `source`
  （或 `blocks` 原文）。Markdown 渲染待后续补齐。

## 24. Phase 6.4：管理端折线图

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/AdminController.cs` | `chartData`（`GET admin/chart`） |

**333 tests / 0 failed；AOT publish 24.2 MB。**

要点：

- 输出 `{labels, xAxis, data}`：`xAxis` 为最近 31 天（含今天），日期按 locale 的
  CLDR 短格式（`zh_CN` → `yyyy/M/d`，`ko_KR` → `yyyy.M.d.`，`de_DE`/`ru_RU` → `d.M.yyyy` 等）。
- `data` 是 `[注册数, 上传数]` 两条 31 元素数组；按「本地化日期串」分组计数。
- `subMonthPhp` 复刻 PHP 的月减法溢出（如 10-31 减一月 = 10-01）。
- JSON 用 `JSON_UNESCAPED_UNICODE` 输出（与源一致）。
- `admin` / `admin/status` 仍是 widget 网格 HTML，等模板引擎。

## 25. Phase 6.5：安装向导与安装锁

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/SetupController.cs` | `database`（探测/试连/写 `.env`）、`finish`（建表/写入选项/建超级管理员/写锁） |
| `Web/Middleware/AuthMiddleware.cs` | `CheckInstallation`（已安装 → 锁定页）、`RedirectToSetup`（未安装 → `/setup`） |
| `Web/Middleware/MiddlewarePipeline.cs` | 装配上述两个中间件（`CreateDefault` 增加 `AppPaths`） |

**337 tests / 0 failed；AOT publish 24.3 MB。**

要点与偏差：

- `POST /setup/database` 用临时连接试连（sqlite/mysql/pgsql），成功后把 `DB_*` 写回
  `.env`（可用 `NEOSKIN_ENV_FILE` 覆盖路径，测试用）。
- `GET /setup/database`：当前连接可用 → 302 `/setup/info`；否则渲染最小表单
  （字段名与源一致）。
- `POST /setup/finish`：校验 → 建表 → 写 `site_name`/`site_url` → 建超级管理员
  （`permission=2`、`verified=1`）→ 写 `storage/install.lock` → 200 最小「安装完成」页。
- `CheckInstallation`：存在 install.lock 时访问 `setup*` 返回「已安装」页。
- `RedirectToSetup`：无 install.lock 时非 `setup*` 请求 302 `/setup`。
- **偏差**：welcome/info/database/finish/locked 页面用最小 HTML 代替 Twig 模板；
  `passport:keys` 与 `migrations` 版本记录表未移植（OAuth 密钥在首次使用 token 端点时生成）。

## 26. Phase 5.8：披风预览

| 文件 | 内容 |
| --- | --- |
| `Service/MinecraftRenderer.cs` | `RenderCape`：裁出披风正面（1,1–11,17）→ 缩放 → 40/20 内边距画布 |
| `Web/Controllers/TextureController.cs` | `preview/{texture}` / `preview/hash/{hash}`（披风）；皮肤预览仍 501 |

**340 tests / 0 failed；AOT publish 24.3 MB。**

要点与偏差：

- 输出尺寸：`宽 = 80 + height/16*10`、`高 = 40 + height`；`height` 默认 200。
- 取样用最近邻（PHP 是 `imagecopyresampled` 双线性），**像素不保证逐字节一致**。
- 皮肤预览需要 3D 等距渲染器（`SkinRenderer`，1000+ 行），暂返回 501；
  披风预览已可用。

## 27. Phase 6.6：Ignition 管理端点

| 文件 | 内容 |
| --- | --- |
| `Config/NeoSkinSettings.cs` | 新增 `Env`（`APP_ENV`）与 `Debug`（`APP_DEBUG`） |
| `Web/Controllers/IgnitionController.cs` | `health-check` / `execute-solution` / `update-config`（含门控） |

**345 tests / 0 failed；AOT publish 24.4 MB。**

要点与偏差：

- 门控等价 `RunnableSolutionsEnabled`：`APP_DEBUG=true` 且 `APP_ENV ∈ {local, development}`
  才放行，否则 403。
- `health-check` 返回 `{"can_execute_commands":false}`（无命令执行能力）。
- `execute-solution`：`solution` 必填（缺失 422），恒 404「Solution could not be found」。
- `update-config`：`theme ∈ {light,dark,auto}`、`editor` 必填、`hide_solutions` 布尔；
  写入 `~/.ignition.json`（可用 `NEOSKIN_IGNITION_FILE` 覆盖），返回 `true/false`。

## 28. Phase 7.1：首页渲染、静态文件与前端语言包

| 文件 | 内容 |
| --- | --- |
| `Support/AssetManifest.cs` | 前端资源哈希表（生产）/ webpack dev-server（debug） |
| `Web/Views/HomePage.cs` | `home.twig` 的等价 HTML 渲染（导航、splash、intro、页脚、copyright、语言/用户菜单） |
| `I18n/JavaScriptBundle.cs` | `front-end` 组 → `blessing.i18n = {...}` |
| `Web/Controllers/HomeController.cs` | `GET /` 首页 + `apiRoot` |
| `NeoSkinApp.cs` | `UseStaticFiles()` + `/lang/<locale>.js` 动态生成 |

**347 tests / 0 failed；AOT publish 24.6 MB。**

要点与偏差：

- 首页输出保持前端契约：`window.blessing`、`#blessing-extra`、`data-mark="nickname"`、
  `#logout-button`、`.locale`、`data-toggle`，资源指向 `/app/*`（哈希名与源产物一致）。
- `debug=true` 时资源指向 `//localhost:8080/app/`（无哈希），与源一致。
- `/lang/<locale>.js` 由语言文件即时生成（含 `JSON_UNESCAPED_UNICODE`）；
  插件语言包暂不合并。
- 静态文件由 `UseStaticFiles()` 从 `wwwroot/` 提供；**部署时需把源项目
  `public/app/` 拷到 `wwwroot/app/`**（约 18 MB，未纳入仓库）。
- 首页是手写模板（不依赖 Thymeleaf 子集渲染器）；其余 HTML 页面仍待模板引擎。
- `meta_extras` / `custom_css` / `custom_js` 原样输出；版权行为 7 分支之一。

## 29. Phase 6.7：通知与公告写侧

| 文件 | 内容 |
| --- | --- |
| `Db/CommunicationRepository.cs` | `Update` / `FindLatestAnnouncement` / `WithdrawAnnouncementsOfOrigin` / `StatusWithdrawn` |
| `Service/Communications.cs` | `Save`（message/announcement/both、投递、重新发布）、`Withdraw`、`Render` |
| `Web/Controllers/CommunicationsController.cs` | `store` / `update` / `withdraw` / `preview` / `importSource` |

**354 tests / 0 failed；AOT publish 24.7 MB。**

要点与偏差：

- `save` 语义：`kind=message` 写站内信并按 audience 投递；`announcement` 覆盖同语言
  最新公告；`both` 先写站内信、发布时再写一条 `audience.communication_id` 指向它的公告。
- 投递只在「首次发布 + 含收件箱类型」时发生（编辑已发布内容不会重复投递）；
  通知载荷含 `title/content/format/communication_id/delivery_token`。
- `withdraw`：状态改 `withdrawn`；`both` 会连带撤回公告。
- `preview` 返回 `{html}` + `Cache-Control: no-store`；`importSource` 按空行切分为
  `text` 块（源用块渲染器的导入算法）。
- **偏差**：Markdown 转换与 HTML 消毒未移植；`source` 原样输出，`blocks` 只支持
  text/heading/alert/collapse/divider 的简化 HTML。`index`/`inbox`/`message` 仍为 HTML 待模板。

## 30. Phase 8.1：插件清单层

| 文件 | 内容 |
| --- | --- |
| `Service/Semver.cs` | 版本比较与约束子集（`*` / `>=` / `^` / `~` / 精确） |
| `Service/Plugin.cs` | 插件目录 + `package.json` 清单（name/title/version/require/icon/readme） |
| `Service/PluginManager.cs` | 多目录扫描、`plugins_enabled` 持久化、依赖/冲突检查、enable/disable/delete |
| `Web/Controllers/PluginController.cs` | `getPluginData` / `manage` / `readme` / `config` |

**361 tests / 0 failed；AOT publish 24.7 MB。**

要点与偏差：

- 启用状态存于 `options.plugins_enabled`（`[{name,version}]`）；版本变化时回写并派发
  `plugin.versionChanged`。
- 依赖：`require` 里的 `blessing-skin-server` / `php` / 其他插件；冲突：`enchants.conflicts`。
  不满足或冲突时启用返回 `{code:1, message:...}`，并在扫描时自动禁用。
- **不执行插件代码**（AOT 无 PHP 运行时）：`config` 恒 404；`readme` 返回最小页
  （Markdown 原样，未转换）。
- `plugins/upload`（ZIP）与 `plugins/wget` 尚未实现。

## 31. Phase 8.2：插件 ZIP 安装与 wget

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/PluginController.cs` | `upload`（multipart zip）/ `wget`（按 URL 下载） |

**364 tests / 0 failed；AOT publish 24.9 MB。**

要点与偏差：

- `upload`：文件必填、Content-Type 需含 `zip`（否则 422），解压到第一个插件目录；
  条目路径做穿越保护；成功后返回 `{code:0}`。
- `wget`：`url` 必填且为 http/https，下载后按同一路径解压；失败返回
  `{code:1, message: admin.download.errors.download}`。
- 与源一致：解压不剥离顶层目录（补丁包/插件包自带目录结构）。
- 插件代码仍不会执行（AOT 限制）。

## 32. Phase 6.8：分页 JSON 与 PHP 对齐

| 文件 | 内容 |
| --- | --- |
| `Web/Pagination/Paginator.cs` | `Body(path,page,perPage,total)`：补齐 `first_page_url / last_page_url / links / next_page_url / path / prev_page_url` |
| `PlayersManagementController` / `UsersManagementController` / `ReportController` / `TranslationsController` | 列表响应改用完整分页体 |

**364 tests / 0 failed；AOT publish 24.9 MB。**

要点：

- PHP 的 `LengthAwarePaginator::toArray()` 包含完整字段（Java 版曾只返回子集）；
  本阶段以 PHP 为准补齐，并保持 `data` 为原始行。
- `path` 为当前请求路径（不含查询串），页码 URL 形如 `path?page=N`（`paginate()`
  默认不带 `withQueryString`）。
- `links` 与源 `bootstrap-4` 视图同构：`&laquo; Previous` / 页码 / `...` / `Next &raquo;`。

## 33. Phase 8.3：插件市场

| 文件 | 内容 |
| --- | --- |
| `Config/NeoSkinSettings.cs` | `Plugins.Registry`（`PLUGINS_REGISTRY`，含 `{lang}` 占位符） |
| `Web/Controllers/MarketController.cs` | `marketData`（列表 + 已安装/可更新/依赖）与 `download`（下载 zip 安装） |

**367 tests / 0 failed；AOT publish 25 MB。**

要点与偏差：

- 注册表：选项 `plugins_registry` 优先，其次 `settings.Plugins.Registry`；逗号分隔多仓库，
  `{lang}` 仅支持 `en` / `zh_CN`（其他语言回退 `en`），与源一致。
- `marketData` 为每个包附加 `installed`（版本或 false）、`can_update`、
  `dependencies {all, unsatisfied}`，并移除 `require`。
- `download`：找不到包/依赖不满足/无 `dist.url` → `{code:1}`；成功下载解压到第一个
  插件目录 → `{code:0}`。
- 注册表拉取失败 → `{code:1, message: admin.plugins.market.connection-error}`。
- 插件代码仍不执行（AOT 限制）。

## 34. Phase 6.9：管理端选项页

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/OptionsController.cs` | `customize` / `score` / `options` / `resource`（GET 自绘表单 + POST 直接持久化） |
| `Service/Option.cs` | 修复 `GetBoolean` 遇到空串/任意字符串时的 `FormatException`（PHP 真值语义） |

**372 tests / 0 failed；AOT publish 25 MB。**

要点与偏差：

- 字段名与源一致（`site_name`、`sign_score_from/to`、`cdn_address` 等）；本地化键写入
  `<key>_<locale>`；复选框缺失写 `"false"`。
- `sign_score` 由 `sign_score_from,to` 组合；`cdn_address` 去尾部 `/`；
  `site_url` 去尾部 `/` 与 `/index.php`。
- `update_source` 非 URL → 表单请求 302 回原页并 flash 错误；JSON 请求 422。
- **偏差**：表单 HTML 为自绘（字段名一致），未复用源 `OptionForm`/Twig 结构；
  尚未实现更新源预设、缓存清理按钮与 `plugins_registry` 正则校验。

## 35. Phase 7.2：安装向导与绑定邮箱的最小页面

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/SimpleViewController.cs` | `Route::view` 的 `setup` / `setup/info` / `auth/bind` 最小 HTML（字段名与源一致） |
| `Web/NeoSkinHandlerResolver.cs` | `Illuminate\Routing\ViewController` 按 uri 分流 |

**373 tests / 0 failed；AOT publish 25 MB。**

要点：

- `GET /setup` 引导到 `/setup/database`；`GET /setup/info` 提供 `site_name/email/nickname/password/password_confirmation` 表单（含 CSRF）。
- `GET /auth/bind` 提供邮箱绑定表单；已绑定邮箱的用户会被 `EnsureEmailFilled` 重定向到 `/user`。
- 其余 ViewController 页面仍待模板引擎。

## 36. Phase 7.3：认证页 HTML

| 文件 | 内容 |
| --- | --- |
| `Web/Views/PageLayout.cs` | 手写 `shared/head` / `shared/foot`（含 `window.blessing`、资源、favicon、SEO、自定义 CSS/JS） |
| `Web/Views/AuthPage.cs` | login / register / forgot / reset / verify 的 `<main>` 外壳与 `#blessing-extra` |
| `Web/Controllers/AuthController.cs` | 5 个视图 action（reset/verify 校验签名，verify 检查 `require_verification`） |

**378 tests / 0 failed；AOT publish 25.1 MB。**

要点与偏差：

- 布局对齐 `auth/base`：`login-page` / `login-box` / `login-card-body`；表单由前端 React 挂载到 `<main>`。
- `login` 的 `#blessing-extra`：`{tooManyFails, recaptcha, invisible}`；`register`：`{player, recaptcha, invisible}`；
  `forgot`：`{recaptcha, invisible}`，并消费会话 `msg`。
- reset/verify 校验临时签名（无效 403）；verify 在 `require_verification=false` 时 403。
- 页面标题为 `<pageTitle> - <siteName>`；`window.blessing.route` 固定为 `/auth`（前端仅按路由名匹配）。
- **偏差**：文案与 DOM 为手写近似（未走模板引擎）；登录表单的实际交互由前端 JS 负责。

## 37. 运行时插件方案（已落地，见 Phase 10）

按 D-001 采用**方案 A：跨平台原生 C ABI**；实现与接线见 `PLUGIN-DESIGN.md`
与 §51–§54：

- 插件导出 `NeoSkinPluginGetApi`，宿主提供函数指针表（log / option / invokeHook），
  参数与返回值用 UTF-8 JSON；
- 加载用 `NativeLibrary.Load/GetExport`，禁用时 `Shutdown` + `Free`；
- 备选 Windows COM 方案不再推进。

## 38. Phase 7.4：通用布局与列表/资料页

| 文件 | 内容 |
| --- | --- |
| `Web/Views/AppLayout.cs` | admin / user / skinlib 三种布局（头部、侧边栏、`.content > .container-fluid`、`#table`、`#usage-box`、`#players-list` 等挂载容器） |
| `Web/Controllers/SimpleViewController.cs` | 8 条 `Route::view` 页面（admin users/players/reports/i18n/plugins×2、user/oauth/manage、skinlib） |
| `Web/Controllers/AdminController.cs` | 仪表盘（统计 + 两个图表容器）与运行状态页 |
| `Web/Controllers/UserController.cs` | 用户面板（`#usage-box`）与个人资料（昵称/密码/邮箱/删除表单） |
| `Web/Controllers/PlayerController.cs` | 角色管理页（`#players-list` + `blessing-extra{count,rule,length,score,cost}`） |

**381 tests / 0 failed；AOT publish 25.1 MB。**

要点与偏差：

- 页面布局为手写近似（模板引擎未接入）；React 组件按 `route.tsx` 的 `el` 挂载到对应容器。
- `blessing-extra` 提供前端需要的初始数据（如角色数、规则文案、积分）。
- 剩余 7 条 HTML：衣柜页、皮肤库详情/上传、举报追踪、通知与公告 3 页；另有 4 条更新管理。

## 39. Phase 7.5：剩余页面（衣柜/皮肤库详情与上传/举报追踪/站内信）

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/ClosetController.cs` | `index`（`#closet-list` + `blessing-extra{unverified,rule,length}`） |
| `Web/Controllers/SkinlibController.cs` | `show`（私密材质鉴权 + `#previewer`/`#side` + extra）、`upload`（`#file-input` + 计费信息） |
| `Web/Controllers/ReportController.cs` | `track`（我的举报表格 + 页码） |
| `Web/Controllers/CommunicationsController.cs` | `index`（发布表单 + 最近 10 条 + 撤回）、`inbox`（站内信列表）、`message`（单条，标记已读） |
| `Db/NotificationRepository.cs` | `InboxPage`（含可见性过滤） |
| `Db/CommunicationRepository.cs` | `ListRecent` |

**384 tests / 0 failed；AOT publish 25.2 MB。路由覆盖 170/174。**

要点与偏差：

- 所有剩余页面均为手写布局/表单（模板引擎未接入），React 按 `route.tsx` 的 `el` 挂载。
- `skinlib/show` 与 `skinlib/info` 使用同一套私密材质鉴权（`status_code_for_private`）。
- `inbox`/`message` 用 `inboxQuery` 语义（已撤回/重新投递的旧通知不可见），`message` 首次打开会标记已读。
- 管理端通知页是最小可用表单（标题/类型/编辑方式/正文/收件人/操作 + 撤回按钮）。

## 40. Phase 6.10：更新管理（UpdateController）

| 文件 | 内容 |
| --- | --- |
| `Config/NeoSkinSettings.cs` | `UpdateSource`（`UPDATE_SOURCE`） |
| `Web/Controllers/UpdateController.cs` | `showUpdatePage` / `download` / `upload` / `install` |

**385 tests / 0 failed；AOT publish 25.2 MB；路由覆盖 174/174。**

要点与偏差：

- 更新信息来自 `option('update_source')` 或 `UPDATE_SOURCE`，要求 `spec=2`，
  含 `latest/php/url/package/sha256`。
- `download`：完整包（`package=full`）校验 SHA-256 后暂存到
  `storage/framework/update/package.zip` 并写入会话 `system_update_package`；
  旧式补丁包直接解压到站点根目录。
- `upload`：multipart 字段 `package`，仅接受 `.zip`、≤64 MB，暂存后提示安装。
- `install`：从会话取暂存包解压到站点根目录（带路径穿越保护），删除临时文件。
- **偏差**：不执行数据库迁移脚本（新版本的迁移需自行运行）；页面为手写布局；
  解压不剥离顶层目录（与源 `Unzip` 一致）。

## 41. Phase 8.4：Markdown 渲染（MarkdownLite）

| 文件 | 内容 |
| --- | --- |
| `Support/MarkdownLite.cs` | GFM 子集：标题/段落/列表/引用/围栏代码/分隔线/强调/行内代码/链接/图片/删除线/硬换行 + HTML 转义 |
| `Service/Communications.cs` | `source` 与每个 block 的内容走 Markdown 渲染 |
| `Web/Controllers/SkinlibController.cs` | 上传页 `contentPolicy` 渲染为 HTML |

**389 tests / 0 failed；AOT publish 25.3 MB。**

要点与偏差：

- 行为对齐 Java `MarkdownLite`；HTML 消毒（Java 的 `CommunicationRenderer.sanitize`）
  仍未移植，输出的 HTML 由可信管理员输入。
- `source` 编辑器内容现在按 Markdown 渲染；`blocks` 的 text/alert/collapse 内容同样渲染。

## 42. Phase 9.1：SkinRenderer 移植（皮肤预览 / 2D、3D 头像）

| 文件 | 内容 |
| --- | --- |
| `Render/Canvas.cs` | GD 风格画布：ARGB 像素、`imagefilledrectangle` / `imagefilledpolygon`、`imagecopy`（source-over）、双线性 `imagecopyresampled`、PNG 编码 |
| `Render/Point3.cs` | `Blessing\Renderer\Point`：`project` / `preProject` / `getDepth`（共享 min/max 用 `Bounds`） |
| `Render/FacePolygon.cs` | `Blessing\Renderer\Polygon`：坐标平面校验、投影、填充 |
| `Render/SkinRenderer.cs` | 完整移植：透明背景修复、新皮肤图层叠加、6 面判定、7 个部位的多边形生成、成员旋转、投影计划、显示顺序、2× 超采样抗锯齿 |
| `Render/CapeRenderer.cs` | 披风正面裁剪（`imagecopyresampled` 带源区域） |
| `Render/Minecraft.cs` | 门面：`RenderSkin` / `Render2dAvatar` / `Render3dAvatar` / `RenderCape` |
| `Service/MinecraftRenderer.cs` | 路由适配层：头像 ratio=25 → resize(size)、皮肤预览 ratio=12、披风 height |
| `Web/Controllers/TextureController.cs` | 头像 `?3d`、皮肤 `preview/*` 接入渲染器（不再 501） |
| `tests/.../Render/RenderSmokeTest.cs` | 9 个烟测：尺寸、像素非空、2D≠3D、64×64 新皮肤、Alex、512px 缩放 |
| `tests/.../Resources/png/{steve,cape}.png` | 源项目测试纹理 |

**399 tests / 0 failed；AOT publish 25.4 MB。**

与 PHP 对齐过程中发现并修正的三个关键点（Java 版在此处有偏差）：

1. **体积点表的键是整数**：PHP 数组键会把浮点截断为 int，因此 `[8 * $hd]` 与循环里的
   `[8]` 是同一个键。这决定了头盔侧面不会覆盖 `z=-2.5/6.5` 的两个角点（否则包围盒多出
   0.18 单位，3D 头像 194×204 而不是 PHP 的 191×203）。Java 版用 `"8"` / `"8.0"` 区分，
   属于偏差；本端口改为整数截断。
2. **`imagecopy` 是 source-over**：GD 默认 `imagealphablending` 开启。`fixNewSkinTypeLayers`
   自拷贝时，第二层透明像素**不会**擦掉基础层（Java 版直接写像素，会把 64×64 皮肤的身体
   和四肢抹成透明，只渲染出头）。`Canvas.BlendPixel` / `CopySelf` 按 source-over 实现。
3. **`imagecolorallocate(0,0,0)` 是不透明黑**：`makeBackgroundTransparent` 在
   `needRemove=false` 时先用不透明黑铺底，再 source-over 贴皮肤；皮肤中的透明像素因此
   显示为黑色而不是空洞。Java 版写成了 `0xFF000000`（GD alpha=127，全透明）。

验证方式：用 PHP 版（`vendor/blessing/texture-renderer`）对同一 `steve.png` 渲染并逐像素比较，
尺寸完全一致（skin 288×268、2D 135×135、3D 191×203），不透明区域 mean per-channel diff
≈ 2–3/255（自研光栅化/重采样与 GD 的差异，见 STATUS）。

## 43. Phase 9.2：Thymeleaf 子集模板引擎

| 文件 | 内容 |
| --- | --- |
| `View/TemplateDocument.cs` | 容错 HTML 解析器（保留文本/注释/DOCTYPE/属性顺序、script 原文、空元素）、模板源与缓存仓库 |
| `View/TemplateExpression.cs` | 表达式词法/语法/求值：字面量、变量、`.`/`[]`、方法调用、`and/or/!/==/!=/</>/+/-/*//%`、三元、`~{...}` 片段表达式、`__${var}__` 预处理 |
| `View/TemplateContext.cs` | 链式作用域、循环状态 `stat`、值语义（成员/索引/迭代/真值/比较/转义）、`#strings`/`#lists`、`view` 对象 |
| `View/TemplateEngine.cs` | `th:text/utext/if/unless/each/block/replace/fragment/switch/case/attr/class/classappend` + URL/表单属性 + `th:selected` + `th:inline="javascript"` |
| `View/SiteViewData.cs` | 共享 composer：base / head / foot / copyright / languages / user-menu |
| `View/TemplateRenderer.cs` | 应用级入口（从 `Resources/views` 加载） |
| `Resources/views/**` | 源项目 86 个模板入库 |
| `Web/Views/HomePage.cs` | 首页改为模板渲染（数据装配不变） |
| `tests/.../View/TemplateEngineTest.cs` | 11 个测试：表达式、片段继承、switch、属性、真实 copyright/home/user-index/communications |

**410 tests / 0 failed；AOT publish 25.5 MB。**

要点与偏差：

- 表达式解析器是手写递归下降，**不使用反射**（AOT 安全）；未知成员/方法按
  `strict_variables = false` 返回 null。
- 片段：支持 `~{template :: fragment(args)}`、`~{::fragment}`、`~{__${var}__ :: frag}`
  与 `th:replace="${frag}"`；参数按片段声明的形参绑定。
- `th:each` 支持 `item` / `item, stat`（`index/count/size/first/last/even/odd/current`）。
- 与 Java/Thymeleaf 实测输出的两处**有意修正**（PHP/Twig 为准）：
  1. `shared/head` 片段根从 `<head th:fragment>` 改为 `<th:block th:fragment>`。
     Java/Thymeleaf 会输出嵌套 `<head>…</head>`，导致 `</head>` 提前闭合、首页
     `<title>`/`home.css` 落到 body；改为 block 后是单一合法 `<head>`。
  2. `th:if="${inline_css}"` 等 null 守卫按 PHP 语义为假。Java 版用 `NullValue`
     哨兵，OGNL 把它当非空对象 → 渲染出空的 `<style></style>`。
- Thymeleaf 的 HTML 模式会移除 `xmlns:th`；本引擎同样不输出该属性。
- `<style>` 内的 `th:block` 会被处理（Java/Thymeleaf 输出原样标签，属于转换 bug）；
  `home.html` 的背景图分支因此能正确渲染。
- 首页已切换到真实模板；其余页面仍为手写布局，后续按模块切换。

## 44. Phase 9.3：认证页 + 用户中心（模板切换）

| 文件 | 内容 |
| --- | --- |
| `View/AuthView.cs` | 认证页变量：`rows`、`extra` JSON、`pageTitle`、共享 composer、`view` 对象 |
| `View/SharedLayout.cs` | 侧边栏颜色、暗色模式、用户面板（头像/徽章）、三段侧边菜单（user/explore/admin） |
| `View/PageView.cs` | `*.base` 公共变量装配 + `shared/grid` 列描述 |
| `View/TemplateContext.cs` | 新增 `TemplateErrors`（`any` / `first` / `all()`） |
| `Web/Views/AuthPage.cs` | 6 个认证页改为模板渲染 |
| `Web/Views/HomePage.cs` | 首页仍走模板（上一阶段） |
| `Web/Controllers/AuthController.cs` | 传入 host/requestPath，标题与路由信息正确 |
| `Web/Controllers/SimpleViewController.cs` | `auth/bind` 改用 `AuthPage.Bind` |
| `Web/Controllers/UserController.cs` | `/user`、`/user/profile` 改用 `PageView` + `user/index`、`user/profile` 模板 |
| `tests/.../AuthLoginHintTest.cs` | 新增绑定页渲染测试（清空邮箱绕过 `EnsureEmailFilled` 重定向） |
| `tests/.../PlayerAndTextureHintTest.cs` | 用户面板/资料页断言改为模板 DOM |

**411 tests / 0 failed；AOT publish 25.5 MB。**

要点：

- 认证页的 `<main></main>` 与 `#blessing-extra` 契约保持不变（前端 React 仍按 JSON 挂载表单）。
- 用户面板的 `gridRows` 直接复用 `shared/grid` + widget 片段：
  `user/widgets/email-verification`、`dashboard/usage`、`dashboard/announcement`；
  个人资料为 `profile/avatar|password|nickname|email|delete-account`。
- 侧边菜单数据按 Java `config/menu.php` 的等价内容硬编码；插件 Hook
  （`side_menu` / `user_badges` / `user_menu`）留待插件阶段。
- 通知下拉已接入 `NotificationRepository.Unread`；用户面板公告已接入 `Communications`。
- `user/index` 的 `announcement` 暂为空串（公告渲染需接 `Communications` 服务）。

## 45. Phase 9.4：用户侧剩余页面 + 皮肤库首页

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/ClosetController.cs` | `/user/closet` → `user/closet` 模板（grid + previewer） |
| `Web/Controllers/PlayerController.cs` | `/user/player` → `user/player` 模板 |
| `Web/Controllers/ReportController.cs` | `/user/reports` → `user/report` 模板（分页 HTML） |
| `Web/Controllers/CommunicationsController.cs` | `/user/inbox` / `/user/inbox/{id}` → 模板 |
| `Web/Controllers/SimpleViewController.cs` | `/user/oauth/manage`、`/skinlib` → 模板 |
| `Web/Pagination/Paginator.cs` | 新增 `LinksHtml()`（Laravel `links()` 的 HTML 形态） |
| `Db/NotificationRepository.cs` | 新增 `CountInbox()`（与 `InboxPage` 同一可见性过滤） |
| `tests/.../PlayerAndTextureHintTest.cs` | 增加衣柜/举报页断言 |
| `tests/.../CommunicationsHintTest.cs` | 增加收件箱空态渲染测试 |

**412 tests / 0 failed；AOT publish 25.5 MB。**

要点：

- `/user/reports` 的分页用 `Paginator.LinksHtml(urlRoot + "/user/reports", page, last)`，
  与 Java `LengthAwarePaginator.linksHtml()` 对齐（previous/页码窗口/next）。
- `/user/inbox` 的 `messages` 为 pager 结构：`items/total/current_page/last_page/
  previous_page_url/next_page_url`；`message_url` 为绝对地址（与源一致）。
- `/skinlib` 使用 `skinlib/base` 顶栏布局，`extra_json = {currentUid}`；
  `/user/oauth/manage` 内容为空（前端按 `window.blessing.route` 挂载）。
- `user/closet`、`user/player` 的 `extra_json` 保留原有字段（`unverified/rule/
  length/score/cost`），前端契约不变。

## 46. Phase 9.5：皮肤库详情 / 上传

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/SkinlibController.cs` | `show` / `upload` 改走模板；`unexistent-user` 走翻译 |
| `tests/.../SkinlibHintTest.cs` | 新增详情/上传页渲染测试 |

**413 tests / 0 failed；AOT publish 25.5 MB。**

要点：

- `skinlib/show`：`gridRows = [md-8 shared/previewer, md-4 skinlib/widgets/show/side]`，
  `extra_json` 保留 `download/currentUid/admin/inCloset/uploaderExists/nickname/report/badges`。
- `skinlib/upload`：`gridRows = [md-6 skinlib/widgets/upload/input, md-6 shared/previewer]`，
  `extra_json` 保留 `rule/privacyNotice/score/scorePublic/scorePrivate/closetItemCost/award/contentPolicy`。
- 详情页标题为材质名；上传页标题为 `skinlib.upload.title`。

## 47. Phase 9.6：管理端页面（列表/仪表盘/通知）

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/SimpleViewController.cs` | `admin/users|players|reports|i18n|plugins/manage|market` → 通用 `AdminPage` + `PageView` |
| `Web/Controllers/AdminController.cs` | `/admin`、`/admin/status` → `admin/index`、`admin/status` + grid |
| `Web/Controllers/CommunicationsController.cs` | `/admin/communications` → `admin/communications`（编辑器/历史/分页） |
| `Db/CommunicationRepository.cs` | `CountMessages()`、`MessagePage()` |
| `Db/NotificationRepository.cs` | `CountRead()`、`CountDelivery()` |
| `tests/.../AdminChartHintTest.cs` | 增加管理端页面断言 |

**413 tests / 0 failed；AOT publish 25.5 MB。**

要点：

- `admin/users` 的 `extra_json = {currentUser}`（`UserRepository.FindJsonById`）；
  其余列表页无 `blessing-extra`（模板本身没有该脚本）。
- 仪表盘 `gridRows = [usage, chart]`；状态页 `gridRows = [status/info, status/plugins]`；
  `detail` 的 `bs/server/db` 结构与 Java 一致。
- 通知与公告：`editor_data`（标题/类型/编辑方式/源码/blocks/语言/收件范围）、
  `editor_data_json`（HEX 转义）、`editing`（id/status/kind）、`pagination.pages`
  （含 `gap` 占位），与模板契约一致。
- `communication-pagination` 使用 `pager` 的 `pages/on_first_page/has_more_pages/...`。

## 48. Phase 9.7：管理端配置页 + 更新页 + 插件 README

| 文件 | 内容 |
| --- | --- |
| `Service/OptionForm.cs` | `App\Services\OptionForm` 的移植：text/checkbox/textarea/select/group/addon/hint/description/message/button 的 HTML 生成 |
| `Web/Controllers/OptionsController.cs` | `customize` / `score` / `options` / `resource` 改走 `admin/*` 模板 + `OptionForm`；POST 逻辑保留 |
| `Web/Controllers/UpdateController.cs` | `/admin/update` → `admin/update` 模板（更新信息/上传/安装/更新日志） |
| `Web/Controllers/PluginController.cs` | `/admin/plugins/readme/{name}` → `admin/plugin/readme` 模板（MarkdownLite） |
| `Support/PatchReleases.cs` | `config/patch.php` 的 9 条更新日志 |
| `View/TemplateContext.cs` | `view.route(name)` 默认路由表（update/communications/user/admin） |
| 测试 | `OptionsHintTest` / `AdminChartHintTest` / `PluginHintTest` 补充模板断言 |

**413 tests / 0 failed；AOT publish 25.5 MB。**

要点：

- 四个配置页保留原有 POST 写入逻辑（`SetOption`/`SetLocalized`/`SetCheckbox`），
  GET 仅改用 `OptionForm` 生成与源 Twig 同构的 HTML。
- `OptionForm` 自动从 `options.<form>.<id>.*` 读取名称/提示/描述/标签/占位；
  `always` 回调用于 updates 表单的默认 URL。
- 更新页的 `patch_releases` 来自 `Support/PatchReleases`；`upload_limits` 以
  64 MB 计（与上传处理器一致）；`view.route()` 通过默认路由表解析。
- 插件 README 使用 `MarkdownLite`（仍无 HTML 消毒，见偏差）。

## 49. Phase 9.8：安装向导 + 错误页 + OAuth 授权页

| 文件 | 内容 |
| --- | --- |
| `View/SetupView.cs` | 安装向导公共变量（locale/view/cdn_base/site_name/version/errors/语言菜单） |
| `Web/Controllers/SetupController.cs` | `/setup/database`、`POST /setup/finish` 改用 `setup/wizard/*` 模板 |
| `Web/Controllers/SimpleViewController.cs` | `/setup`、`/setup/info` 改用模板 |
| `Web/Middleware/AuthMiddleware.cs` | `CheckInstallation` 渲染 `setup/locked`（数据库不可用时回退最小页） |
| `Error/ErrorPages.cs` | 403/404/500/503 → `errors/base`；401/402/419/429 → `errors/minimal`；其余 → `errors/symfony` |
| `Web/RouteDispatcher.cs` | HTML 错误分支接入 `ErrorPages`（JSON 分支不变） |
| `Web/Controllers/PassportOAuthController.cs` | `/oauth/authorize` → `oauth/authorize` 模板 |
| `NeoSkinApp.cs` / `MiddlewarePipeline.cs` | 传入 option/settings 供错误页/安装锁页使用 |
| 测试 | 405 Symfony 错误页；安装向导断言按模板文案更新 |

**414 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- 错误页在数据库不可用（如 Phase 3 未接入的 MySQL 驱动）时仍能渲染：
  `ErrorPages` 对 `Option` 读取做兜底（默认站点名/主题色），避免错误页本身 500。
- 安装锁页由 `CheckInstallation` 中间件渲染，已安装时访问 `/setup*` 显示
  `setup/locked`。
- OAuth 授权页改用模板；scope 描述从 `OAuthService.Scopes` 翻译。
- 至此 **86/86 个源模板**全部进入渲染路径（`auth/base` 等共享片段由其他模板引用）。

## 50. Phase 9.9：通知下拉 + 用户面板公告

| 文件 | 内容 |
| --- | --- |
| `Service/Communications.cs` | 新增 `Announcements(locale)`（当前语言优先、空语言兜底，仅已发布） |
| `View/PageView.cs` | 静态 `InboxProvider` / `AnnouncementProvider`（对应 Java 的静态注入），渲染 `notifications_json` |
| `Web/NeoSkinHandlerResolver.cs` | 组合根注入：未读通知 `{id,title}` + 公告 HTML |
| `Web/Controllers/UserController.cs` | 用户面板 `announcement` 接入 `Communications` |
| `tests/.../CommunicationsHintTest.cs` | 公告与未读通知在 `/user` 的渲染测试 |
| `tests/.../AssemblyInfo.cs` | 关闭测试并行（静态 provider 是进程级状态） |

**415 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- `shared/notifications` 的 `data-notifications` 现在输出未读通知 `[{id,title}]`；
  公告在用户面板 `card-body` 原样输出（Markdown 渲染后的 HTML）。
- 公告查找与 Java `currentAnnouncement(locale)` 一致：先当前语言、再空语言，
  仅 `status=published` 才渲染。
- PageView 的 provider 是静态的（与 Java 同构）；xUnit 默认并行会让多个测试 App
  互相覆盖，因此测试程序集改为串行执行。

## 51. Phase 10：运行时原生插件（方案 A 最小切片）

| 文件 | 内容 |
| --- | --- |
| `src/NeoSkin.Plugin.Abstractions/PluginAbi.cs` | ABI v1：`NeoSkinHostApi` / `NeoSkinPluginApi`、错误码、入口名 |
| `src/NeoSkin.Web/PluginHost/NativePluginHost.cs` | `NativeLibrary.Load`、`GetApi` 校验、`filter:*` / `event:*` 调用、`Shutdown` |
| `src/NeoSkin.Web/PluginHost/PluginRuntime.cs` | 扫描已启用且声明 `native` 的插件；Start/Stop/Reload；失败派发 `PluginBootFailed` |
| `src/NeoSkin.Web/Service/Plugin.cs` | `package.json` 的 `native` 字段 |
| `NeoSkinApp.cs` | 启动加载、`ApplicationStopping` 卸载 |
| `PluginController.Manage` | 启用/禁用/删除后 `Reload()` |
| `samples/NeoSkin.Plugin.Sample` | NativeAOT 共享库示例（`filter:user_badges` / `filter:head_links`） |
| `tests/.../Plugin/NativePluginHostTest.cs` | ABI、加载、filter、未知 topic、清单校验、运行时发现 |

**421 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- ABI v1 在同一次会话中新增 `Free` 回调：插件分配并释放结果内存，宿主只复制字节，
  避免跨模块分配器不匹配。
- 无插件目录时 `PluginRuntime.Start()` 完全不访问数据库，避免无插件站点启动时建立连接。
- 启用/禁用插件后重载运行时；插件库卸载前调用 `Shutdown`。
- **未接线**：业务页面的 Hook 调用点（如 `user_badges` 进入用户面板）、插件语言包合并。

## 52. Phase 10.1：`user_badges` 过滤器接线

| 文件 | 内容 |
| --- | --- |
| `View/SharedLayout.cs` | `FilterUserBadges()`：把徽章数组序列化后交给 `PluginRuntime.Filter`，解析插件返回的 JSON |
| `View/SiteViewData.cs` | 修复 `UserMenu` 覆盖 `badges` 的问题（此前管理员 STAFF 徽章也会被清空） |
| `tests/.../PluginRuntimeHintTest.cs` | 端到端：站点启动时加载示例插件，`/user` 显示 `SAMPLE` 徽章 |

**422 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- 过滤器输入/输出都是 JSON 数组（`[{text,color}]`）；插件无结果时保留原数组，
  异常由 `PluginRuntime` 记录并回退。
- 示例插件 `filter:user_badges` 现在真正进入用户面板。
- 部分平台上 NativeAOT 共享库卸载后仍持有文件句柄，测试清理临时目录时忽略
  `UnauthorizedAccessException`。

## 53. Phase 10.2：渲染层 Hook 与插件语言包

| 文件 | 内容 |
| --- | --- |
| `View/PluginFilters.cs` | 列表/字符串过滤器适配（JSON 往返、异常回退） |
| `View/SiteViewData.cs` | `head_links`、`scripts`、`user_menu`、`user_avatar` 接入 |
| `View/SharedLayout.cs` | `side_menu`、`user_badges` 使用统一过滤器 |
| `Web/Controllers/SkinlibController.cs` | `content_policy` 过滤器 |
| `I18n/JavaScriptBundle.cs` | 合并已启用插件 `lang/<locale>/front-end.yml`（以插件名为键） |
| `samples/NeoSkin.Plugin.Sample` | 示例 `head_links` / `scripts` / 语言包 |
| 测试 | 插件端到端断言插件样式/脚本/语言包 |

**422 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- 过滤器输入/输出统一 JSON；插件抛错/返回损坏 JSON 时保留原值。
- `head_links` / `scripts` 的结果直接进入 `shared/head`、`shared/foot` 的循环。
- `/lang/<locale>.js` 现在包含 `"<PluginName>": { ... }`；主表从 `Translator` 取
  （`PhpArray` 或字典均支持）。
- 修复 `JavaScriptBundle` 在无插件时把空表编码成 `[]` 的问题（应为非空主表）。
- 认证流程中的过滤器（`can_login` / `verify_password` / `client_ip` /
  `can_register` / `user_password` / `auth_page_rows:*`）仍未接入。

## 54. Phase 10.3：认证类插件过滤器

| 文件 | 内容 |
| --- | --- |
| `Web/Controllers/AuthController.cs` | `can_login`、`verify_password`、`client_ip`、`can_register`、`user_password` |
| `View/AuthView.cs` | `auth_page_rows:login` / `auth_page_rows:register` |
| `samples/NeoSkin.Plugin.Sample` | 示例支持认证过滤器（`blocked@` 账号被拒绝） |
| `tests/.../PluginRuntimeHintTest.cs` | 端到端断言 `can_login` 拒绝码 1 + 原因 |

**423 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- `can_login` 先于用户查找执行；插件返回非空字符串即拒绝（JSON 字符串）。
- `verify_password` 收到 `{passed,password,user}`，返回 JSON 布尔；非布尔回退原值。
- `can_register`、`client_ip`、`user_password` 分别作用于注册入口、IP 解析、密码哈希。
- `auth_page_rows:*` 作用于登录/注册页的行模板列表。
- 至此 Java 版用到的全部过滤器均已接线；PHP 插件仍需按 ABI 重写。

## 55. Phase 10.4：HTML 消毒（HtmlSanitizer）

| 文件 | 内容 |
| --- | --- |
| `Support/HtmlSanitizer.cs` | 白名单标签/属性、id 前缀重写、class/style 过滤、`a/button/img` 属性、`data-target`/`aria-controls`/`href` 引用同步 |
| `Service/Communications.cs` | `RenderData` 输出统一消毒；collapse 改为 Java 同构的 card 结构；tone 白名单 |
| `Web/Controllers/PluginController.cs` | 插件 README 的 Markdown 输出消毒 |
| `tests/.../HtmlSanitizerTest.cs` | 脚本/事件属性/危险 URL、id 引用、class/style、Markdown 保留 |

**427 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- 使用自研 `TemplateParser` 解析片段并序列化，无反射、AOT 安全。
- 标签白名单/移除表、属性规则、id/class/style/URL 正则与 Java 版逐条对齐。
- `collapse` 块改为 `card card-outline card-<tone>` + `data-target="#block-N"`，
  消毒时统一改写为 `<prefix>-N`，按钮的 `aria-controls` 同步。
- 公告渲染前缀 `announcement-<id>`、站内信 `mail-<id>`（对应 Java `renderer.render`）。

## 56. Phase 11：更新安装的数据库迁移

| 文件 | 内容 |
| --- | --- |
| `Db/SchemaMigrator.cs` | Java `DataMigrator` 的移植：import options / max_texture_width / create_communications / OAuth secret 哈希；按 `migrations` 表幂等 |
| `NeoSkinApp.cs` | 站点已安装时启动执行待迁移项（SQLite 完成后清理连接池） |
| `Web/Controllers/UpdateController.cs` | 完整包/补丁包解压后执行迁移 |
| `tests/.../Db/SchemaMigratorTest.cs` | 迁移登记、二次为空、不覆盖已有选项、OAuth secret 哈希 |
| `tests/.../OptionsHintTest.cs` | 默认选项导入后 `update_source` 为空而非 null |

**430 tests / 0 failed；AOT publish 25.6 MB。**

要点：

- `import_options` 只在 options 表为空时灌入，避免升级覆盖站点配置；
  `max_texture_width` 仅在缺失时写 8192。
- `hash_oauth_client_secrets` 把非 `$2*` 的 secret 用 bcrypt（cost 10）哈希。
- `create_communications` 目前只保证表存在；旧 PHP 公告/站内信回填未实现。
- 更新解压后会执行迁移；服务重启后启动路径也会补齐遗漏项（幂等）。

## 57. Phase 11.1：旧 PHP 公告/站内信回填

| 文件 | 内容 |
| --- | --- |
| `Db/SchemaMigrator.cs` | `create_communications` 数据回填：options 公告 + notifications 站内信 + `communication_id` 回写 |
| `tests/.../SchemaMigratorTest.cs` | 公告/站内信回填、audience、幂等重跑 |

**431 tests / 0 failed；AOT publish 25.7 MB。**

要点：

- 公告：`options` 中 `announcement` / `announcement_*`（非空、trim 后）→
  `kind=announcement`、`legacy_key='option:<name>'`、`locale` 取键后缀。
- 站内信：按 `notifications.id` 升序每页 200 条；`legacy_key='notification:<id>'`，
  标题取 `data.title`（mb_substr 100），正文取 `data.content`，
  audience 为 `{"receiver":"uid","uid":<notifiable_id>}`。
- 回写 `notifications.data.communication_id`（键不存在时追加）；
  已存在且指向有效 communication 的记录跳过。
- `legacy_key` 唯一性保证重复执行不重复插入。
- 回填后的通信行使用源侧渲染管线（Markdown + 消毒）。

## 58. 下一步

1. 浏览器 E2E 回归纳入 CI（Phase 12 已手工覆盖主要流程）；
2. 视觉回归与性能门槛（Phase 12 已有初步性能数据）；
3. 插件市场安装/上传端到端测试；
4. ~~版本升级的请求期编排~~（已完成：`Service/UpdateRunner` +
   `RedirectToSetup`，含 5.0.0 背景图迁移、版本号、迁移、`view:clear`、补锁、清缓存）。
## 59. Phase 12：浏览器 E2E、图像输出与多驱动

| 文件 | 内容 |
| --- | --- |
| `Web/Input/RequestInput.cs` 接入点 | `AuthController` / `PassportApiController` / `PassportOAuthController` 写请求统一走 query+form+JSON |
| `Db/CommunicationRepository.cs` | 新增 `FindCurrentAnnouncement(locale)`（源 `currentAnnouncement()`：当前语言 → 空语言；`announcement` 或已发布 `both`；`updated_at desc, kind, id desc`） |
| `Service/Communications.cs` | `Announcements()` 改用 `FindCurrentAnnouncement` |
| `Service/ImageCache.cs` | 进程内图片缓存（`Cache::remember` 语义，512 条粗粒度清空） |
| `Web/Controllers/TextureController.cs` | 头像/预览默认 WebP（`?png` 回退 PNG）、ETag/304/Last-Modified、`enable_avatar_cache` / `enable_preview_cache` |
| `Db/Database.cs` | MySqlConnector / Npgsql / Microsoft.Data.SqlClient 连接工厂 |
| `Db/DatabaseUrl.cs` | `DATABASE_URL` 解析（scheme → 驱动、userinfo/host/port/path） |
| `Db/DbRead.cs` | 驱动返回值归一化（时间戳 → PHP 字符串、INT/BIGINT → long） |
| `Db/SchemaInstaller.cs` | 修正已打开连接被重复 `Open()`（非 SQLite 会抛异常） |
| `tests/.../Db/DatabaseMatrixTest.cs` | 5 库矩阵：建表 + 用户/纹理 CRUD + 时间戳/布尔 |
| `.github/workflows/ci.yml` | 5 数据库矩阵 + linux-x64 Native AOT 发布 |
| `DB-MATRIX.md` | 本机 `tools/scripts/db.cmd` 与 CI 矩阵操作说明 |
| `tests/.../Db/DatabaseUrlTest.cs` | URL 解析与三种连接串 |
| `tests/.../Web/PlayerAndTextureHintTest.cs` | WebP 默认输出 + `?png` 像素断言 |
| `tests/.../Web/PassportHintTest.cs` | OAuth clients 接受 JSON 请求体 |
| `tests/.../Web/CommunicationsHintTest.cs` | 公告编辑器的空语言回退 |

**446 tests / 0 failed（矩阵测试在无 DB 环境时 Skip）；AOT publish 43.1 MB（含三个数据库驱动）。**

要点：

- 浏览器 E2E 覆盖：首页、登录/注册/验证码、用户中心全部页面、皮肤库/详情/上传、
  玩家创建与材质应用、Minecraft `.json`、OAuth2 授权码全流程（client → authorize →
  code → token → scopes 403）、管理端全部页面、404、语言切换、公告发布。
- 发现并修复：Passport 写接口原先只读 form/query，前端 `blessing.fetch` 发 JSON
  导致创建 OAuth 应用 422；公告编辑器未回退到 `locale=''`，导致默认语言公告打不开。
- 头像/预览此前固定 PNG，而 `<source type="image/webp">` 会按 WebP 解码；现按源
  `imagetypes() & IMG_WEBP` 语义默认 WebP，PNG 走 `?png`。
- 图片缓存键与源一致（预览键不含 height）；ETag 使用 SHA-256（源为 xxh128，
  .NET 无内置实现，Java 版同样用 SHA-256）。
- `Microsoft.Data.SqlClient` 在 Native AOT 下报 IL2104/IL3053 裁剪警告，发布与
  WiltonDB（SQL Server 协议）连接路径均已实测可用；后续如需更严格可标注
  `TrimmerRootAssembly`。
- 本机数据库矩阵（`tools/scripts/db.cmd`）实测通过：MySQL 26.10（3307）、
  MariaDB 13.0.2（3308）、PostgreSQL 18.6（5433）、WiltonDB TDS（1433）。
  `DatabaseMatrixTest` 覆盖建表 + 用户/纹理 CRUD + 时间戳/布尔/自增主键；
  AOT 产物对四个库的 `/skinlib/list`、`/auth/login`、`/api` 均 200。
  过程中修复：`SchemaInstaller` 对已打开连接重复 `Open()`（MySQL/MariaDB 必现）；
  Npgsql 字符串参数需发 unknown（等价 pdo_pgsql 隐式转换）；PG 时间戳读取需格式化；
  SQL Server 的 `public` 是保留字必须引用；`INT` 列统一用 `long` 读取。
- 首次 CI（linux）修复：`CookieCorpusTest` 两个用例在缺少 `conformance/data` 时抛异常
  → 改用 `[ConformanceFact]` skip；`CommunicationsHintTest` 移除写死
  `/tmp/opencode/dash.html` 的调试语句；SQL Server `SCOPE_IDENTITY()` 必须与
  `INSERT` 同一批次（单独命令在真 SQL Server 返回 NULL，WiltonDB 未暴露）。
  CI 矩阵（sqlite/mysql/mariadb/pgsql/sqlsrv + linux-x64 AOT）已全绿。
  CI 矩阵见 `.github/workflows/ci.yml`，本机步骤见 `DB-MATRIX.md`。
- 性能基线（AOT、300 请求）：`/` c=1 ≈ 432 rps（p50 1.7ms）、c=8 ≈ 920 rps（p50 8.1ms）；
  `/auth/login` c=8 ≈ 982 rps；`/skinlib` c=8 ≈ 817 rps；`/avatar/1` c=8 ≈ 3160 rps
  （p50 2.4ms）；`/lang/en.js` c=8 ≈ 1190 rps。
- 语言包加载器加入与 Laravel `FileLoader::$loaded` 等价的解析缓存；此前每个
  `trans()` 都读盘并解析 YAML，首页 p50 约 15ms / 57 rps，缓存后降至 1.7ms / 432 rps。

## 60. Phase 13：请求期自动升级与 CLI（2026-11-06）

| 文件 | 内容 |
| --- | --- |
| `Service/UpdateRunner.cs` | PHP `UpdateCommand`：0.0.1 事件、5.0.0 `home_pic_url`、版本号、`SchemaInstaller` + `SchemaMigrator`、`view:clear`、`install.lock`、清缓存 |
| `Web/Middleware/AuthMiddleware.cs` | `RedirectToSetup` 已安装且版本落后时调用 `UpdateRunner.RunIfNeeded()`；`setup*` 改为按路径段匹配 |
| `Cli/CliRunner.cs` | `--neoskin.command=install|bs:install|update|options:cache|plugin:enable|plugin:disable|salt:random`，CLI 模式不启动 HTTP 监听 |
| `Program.cs` | 先分流 CLI，再启动 Web |

- `UpdateRunner` 用静态锁串行化，并在锁内重新检查版本；失败异常在中间件层降级为日志，
  避免数据库临时故障放大成所有请求 500。
- CLI 的 `install` 在缺 `APP_KEY` 时生成并写入 `.env`；`plugin:enable` 对不存在的插件
  返回 1（PHP 的 not-found 语义）。
