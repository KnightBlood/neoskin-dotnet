# AGENTS.md — NeoSkin.NET (.NET 10 / slnx) 迁移约束

本文件约束所有 agent 在 `NeoSkin.NET/` 目录内的行为。

---

## 1. 目标与基线

- **目标**：将 `blessing-skin-server-Laravel13`（6.0.11_patch）完整迁移到 .NET 10 + ASP.NET Core，
  **Native AOT** 发布（`dotnet publish -p:PublishAot=true`），使用 `.slnx` 解决方案格式。
- **唯一参考实现（权威）**：`D:\WORKspace\NeoSkin\blessing-skin-server-Laravel13`（PHP，只读）。
  路由、表结构、请求/响应字节、模板 DOM、PHP 语义全部以它为准。
- **已验证的参考移植（非权威）**：`D:\WORKspace\NeoSkin\NeoSkinSpringBoot`（Java 21 版，
  174 条路由 / 1085 tests 全绿）。它的实现、PHP 兼容层、视图 HTML、语料用例可以**直接复用**，
  但**任何冲突以 PHP 源码为准**，并记入本目录 `PORT-NOTES.md`。
- **一致性语料**：`D:\WORKspace\NeoSkin\conformance\data\*.json`（PHP 侧生成，四语言共用，禁止手改）。

## 2. 硬约束（不可破坏）

1. **HTTP 接口零变更**：路由集合以 PHP 源码为准。`codegraph/routes.json` 是 173 条，
   但源码里 `POST skinlib`（`SkinlibController@handleUpload`）被漏记，实际是 **174 条**
   （Java 版已按源码补齐，本端口同样按 174 条断言）。
2. **数据完全兼容**：17 张表 + 存量数据 + bcrypt 密码（BCrypt.Net-Next 支持 `$2y$`）+ SHA-256 纹理 hash。
3. **插件 API 契约**：Hook（7 方法）、PluginManager（13 方法）、Plugin、24 个 Event。
4. **patch 功能保留**：6.0.3 ~ 6.0.11_patch 全部功能。
5. **禁止修改源目录**。
6. **前端直接复用，不自己重写**：`resources/assets/`（前端源码）与
   `public/app/`（预编译产物）原样复用源项目，**不重新打包、不换技术栈、
   不做 SPA 改造**。静态产物拷贝到 `wwwroot/app/`，`/lang/<locale>.js`
   由后端生成。服务端模板（Twig）是**前端契约**：`window.blessing`、
   `#blessing-extra`、`#logout-button`、`data-mark="nickname"`、`.locale`、
   `<main></main>`、`data-*` 钩子必须逐字保住。详见 `MIGRATION.md §9.0`。

## 3. 技术栈锁定（AOT 优先）

AOT 是硬要求，因此**不能用** EF Core（反射/动态代理）、OpenIddict（反射）、
运行时 Razor 编译、`AssemblyLoadContext` 动态插件、反射式 JSON 序列化。
选型如下（语义对齐 Java 版）：

| 组件 | 版本 / 选型 | 说明 |
| --- | --- | --- |
| .NET | 10.0（C# 14） | Native AOT 发布 |
| Web | ASP.NET Core 10 minimal API + 自研路由表 | 对应 Java 的 `VertxRouteRegistry`，按 `routes.json` 注册 174 条 |
| 数据访问 | ADO.NET（Sqlite / MySqlConnector / Npgsql / SqlClient） | 手写方言与 SQL，对应 Java 的 `SchemaInstaller` + `SqlDialect` |
| 模板 | 自研 Thymeleaf 子集渲染器 | **逐字复用 Java 版 86 个已验证 HTML 模板**（`th:*` 子集） |
| OAuth2 | 自研 Passport 兼容实现 | 与 Java 版同构（`oauth_*` 表 + 令牌格式） |
| 时间 | NodaTime | IANA tzdata（Windows 的 TimeZoneInfo 缺历史偏移，上海 1901 年前是 +08:05:43） |
| YAML | YamlDotNet（只做节点解析）+ 自研 Symfony 标量规则 | 语言包 `Resources/lang/*.yml` |
| 图像 | ImageSharp / 自研 PNG 编码 | 目标是与 PHP Imagick 字节级一致 |
| 序列化 | System.Text.Json source generators | 禁止反射式序列化 |
| 密码 | BCrypt.Net-Next | 兼容 `$2y$` |
| 插件 | 编译期契约 + 内置插件 | AOT 无法在运行时加载 IL 程序集（见 §4 第 5 条） |
| 解决方案 | `.slnx` | — |


## 4. 禁止事项

1. 禁止改变数据库表结构语义。
2. 禁止破坏插件 API。
3. 禁止改动 `.env` 运行时行为（appsettings.json）。
4. 禁止提交 `bin/`、`obj/`、`node_modules/`、`.env`、`publish/`。
5. **AOT 禁止项**：
   - 反射式 JSON（必须 source generator）；
   - EF Core / 任何依赖动态代理或运行时表达式树的 ORM；
   - 运行时编译 Razor / 动态生成代码；
   - `AssemblyLoadContext` 运行时加载插件 DLL（AOT 无 JIT，加载即失败）。
6. 禁止手改 `conformance/data/*.json`（要改用 `conformance/oracle/` 重新生成）。


## 5. 代码风格

- 遵循 .NET 标准（dotnet format）。
- 注释与文档使用中文，代码标识符使用英文。

## 6. 验证清单

```sh
dotnet build NeoSkin.NET.slnx
dotnet test
```

- [ ] `dotnet build` 无错误
- [ ] 所有 173 条路由注册成功
- [ ] 数据库 migration 执行无错误
- [ ] 存量数据可正常读取
- [ ] bcrypt 密码验证通过
- [ ] OAuth2 端点与 Passport 响应一致
- [ ] Minecraft 客户端可正常获取皮肤/披风
- [ ] 测试全部通过（含 conformance 语料）

## 7. 进度报告格式

agent 完成一个阶段后必须报告：
1. 已完成的 MIGRATION.md 阶段编号。
2. 修改的文件清单。
3. 失败/阻塞项与原因。
4. 下一阶段待办。

---

## 8. 源代码图谱（CodeGraph）

源项目 `D:\WORKspace\NeoSkin\blessing-skin-server-Laravel13`（只读）已生成机读代码图谱，移植时**优先读取它**，不要靠反复 grep 源码推断契约。

| 文件 | 内容 | 用途 |
| --- | --- | --- |
| `CODEGRAPH.md` | 架构总览、17 张表、路由分组、插件 API | 先读这一份建立全局认知 |
| `codegraph/routes.json` | 173 条路由（method / uri / name / action / middleware） | 1:1 生成路由注册代码；源码另有 `POST skinlib`，实际 **174** |
| `codegraph/controllers.json` | 20 个控制器类名 | 对照拆分 Controller |
| `codegraph/models.json` | 6 个 app 层 Eloquent 模型 | 对照生成 Entity/Model |
| `codegraph/events.json` | 24 个 Event + 9 个 Listener | 事件总线移植 |
| `codegraph/services.json` | 11 个 Service | 业务层移植 |
| `codegraph/middleware.json` | 12 个中间件 | 过滤器/中间件链移植 |

使用纪律：

1. 路由以 `routes.json` 为唯一权威，**不得增删改**路径、方法或参数名。
2. 表结构以 `database/migrations/` 为准（`codegraph` 不含字段级 DDL），字段名/类型/默认值必须逐字对齐。
3. 若发现 `codegraph` 与源码不一致，以源码为准，并在本文件记录偏差。
4. 迁移过程中新增的任何映射关系，追加到本目录 `PORT-NOTES.md`，不回写源项目。
