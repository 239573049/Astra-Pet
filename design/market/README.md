# 宠物市场（Astra Hub 客户端）

控制台新增「市场 → 宠物市场」一页：登录 Astra Hub 账号，浏览市场上的角色，免费 / 已拥有的直接下载安装，付费的跳到浏览器购买。
服务端是独立项目 Astra Hub（接口见它的 `design/10-api.md` §8），这里只写桌面端。

## 代码位置

按 [01-layering.md](../01-layering.md) 分层：

| 层 | 文件 | 内容 |
|---|---|---|
| Core | `Ports/IHubClient.cs` | HTTP 接口（Port）：设备码、轮询、me、logout、列表、下载、取封面 |
| Core | `Market/MarketModels.cs` | DTO 记录、`MarketHub.BaseUri`（固定地址）、`HubException`、价格格式化 |
| Core | `Market/MarketService.cs` | 登录轮询、401 处理、列表、安装状态、下载安装与校验 |
| Core | `Market/HubSessionStore.cs` | 令牌文件 `hub-session.json` |
| Infrastructure | `Market/HttpHubClient.cs` | HttpClient + 信封解析 + 源生成 JSON |
| App | `ControlPanel/MarketViewModel.cs`、`MarketPanel.axaml` | 页面状态、卡片、封面缓存 |
| App | `Composition.cs` | 组装：`HubClient`、`Market`（全程序一个，控制台释放重建后登录状态还在） |

## 服务器地址：固定，只能在构建时改

地址是 `https://hub.astra-gate.si`，写在 `Directory.Build.props` 的 `HubBaseUrl`，由 `AstraPet.Core.csproj` 的 `GenerateBuildInfo`
写进 `BuildInfo.HubBaseUrl` 常量（和 `UpdateBaseUrl` 同一套做法，不靠反射读程序集特性，NativeAOT 下可靠）。

- **界面上不显示**，设置里没有这一项，也**没有环境变量覆盖**。
- 开发时连本地的 Hub：`dotnet build -p:HubBaseUrl=http://localhost:5000`（发布包不会带这个参数）。

为什么不让用户改（更新服务器是可以改的）：市场要登录，令牌是 Bearer。地址能改，就能被诱导填一个假的「市场」，
用户在那里确认登录、令牌和下载的角色包都来自别人；固定地址省掉这一整类问题，也没有真实的改地址需求。

## 登录：设备码流程

和 OAuth 设备流程（RFC 8628）一样，桌面端不碰密码：

1. `POST /api/device/code { clientName }`。clientName 是「Astra Pet 0.1.4 · macOS」，用户在网页的「已连接的设备」里看到。
2. 用浏览器打开 `verificationUriComplete`，页面上**大字显示 userCode**（ABCD-EFGH）让用户和网页核对；网址不显示。
   「重新打开浏览器」再开一次，「取消」停止轮询。
3. 每隔 `interval` 秒 `POST /api/device/token { deviceCode }`：

| 返回的 code | 处理 |
|---|---|
| `device.authorization_pending` | 接着等 |
| `device.slow_down` | 间隔加 5 秒 |
| `device.expired_token` / `device.access_denied` / `device.invalid_code` / 其他 | 停下，显示服务器的 message |
| 网络错误（`net.*`） | 记日志，接着等 |
| 累计等满 `expiresIn` | 停下，「登录码已过期」 |

4. 成功拿到 `accessToken` 后 `GET /api/client/me` 取昵称，存下登录状态，拉列表。

轮询期间控制台算「忙」（`ConsoleViewModel.Busy`），收起后不会被释放掉。

## 令牌存储

- 单独一个文件：数据目录（`IAppPaths.DataDirectory`）下的 `hub-session.json`，内容是令牌、昵称、用户 id、登录时间。
  **不进 settings.json**：设置文件会被拷来拷去、发给开发者排查问题。
- Unix 上权限 0600：临时文件创建时就带 `UnixCreateMode = UserRead | UserWrite`，改名前再设一次，最后原子改名。
- 令牌不写日志；HTTP 客户端只把它放在 `Authorization` 头里，而且**只发给 Hub 自己的主机**（下载地址换了主机直接拒绝）。
- 退出登录：先 `POST /api/client/logout` 让服务器吊销（10 秒超时，失败也继续），再删本地文件。
- 任何带令牌的请求返回 **401**：删掉本地文件，回到未登录，提示「登录已过期，请重新登录」。

## 页面状态

| 状态 | 内容 |
|---|---|
| 未登录（默认） | 一段说明 + 「登录」 |
| 登录中 | 大字 userCode、「正在等待确认…」、「重新打开浏览器」「取消」 |
| 已登录 | 昵称 +「退出登录」；搜索框（回车搜索）、排序（热门 / 最新 / 下载 / 评分）、「刷新」；卡片列表；有 `nextCursor` 时「加载更多」 |

列表在第一次真正打开市场页时才拉（控制台预热会把每页切一遍，那时不发请求），之后靠「刷新」。
在浏览器里买完回来点「刷新」，`needPurchase` 就变了。

卡片：封面（16:9）、标题、价格（`免费` / `¥9.90` / `已拥有`，金额是分）、作者、简介、评分和下载数、版本。按钮：

| 条件 | 按钮 |
|---|---|
| `needPurchase` | 「购买 ¥x.xx」→ 浏览器打开 `pageUrl` |
| 不能下载（`canDownload = false`） | 「不可下载」置灰，下面显示 `reasonMessage` |
| `minAppVersion` 比当前版本新 | 「需要新版本」置灰 |
| 没装 | 「下载」，下载时显示进度 |
| 装了、SHA-256 和列表一致 | 「已安装」置灰 +「使用」 |
| 装了、SHA-256 不一致 | 「更新」+「使用」 |

「使用」走控制台「角色」页同一条切换路径（重新扫描角色目录 → 设 `SelectedPet` → 后台加载 → 成功才换）。
本机安装状态要读整个文件算哈希，在后台线程算，按大小和修改时间缓存。

封面：`coverUrl` 是 webp，Avalonia 的 Skia 能解（工作台已经在用）；异步下载、`Bitmap.DecodeToWidth(480)` 解码，
按地址放在内存缓存里（上限约 120 张），下载或解码失败显示占位图。

## 安装与校验

`MarketService.InstallAsync`：

1. 下载前检查：`packId` 只能是小写字母、数字、短横线（要拿来当文件名）；不能和内置角色包的 id 重名（不覆盖、也不混淆内置角色）；
   `minAppVersion` 满足；下载地址是 Hub 自己的。
2. 带 Bearer 下载到**角色目录里**的隐藏临时文件 `.{packId}-{guid}.part`（和目标同一个卷，最后的改名是原子的；
   角色目录扫描只认 `*.astrapet`，不会把它当角色）。超过 1 GB（和 `PetPack` 解压上限一致）就中止。
3. 校验：SHA-256 以响应头 `X-Pet-Sha256` 为准（列表拿到之后作者可能刚发了新版），没有就用列表里的 `sha256`；
   下载的就是列表上那一版时，大小还要和 `fileSize` 一致。服务器两个哈希都没给时只记日志。
4. `PetPack.Inspect` 检查：文件白名单、大小上限、必需的动作，以及**格式版本**（比 `PetPack.SupportedFormat` 新的包会被拒，
   提示升级 Astra Pet）。清单里的 id 也不能和内置角色重名。
5. 改名为 `{角色目录}/{packId}.astrapet`，覆盖旧版本；刷新角色列表，新角色出现在「角色」页。

任何一步失败都删掉临时文件，原来装着的版本不受影响，错误显示在卡片上。

## 错误处理

- 所有 JSON 响应都是信封 `{ success, code, message, data, traceId }`；`success:false` 时原样显示 `message`（服务器已经写成中文）。
- 401 → 清令牌、回到未登录。
- 连不上、超时（API 30 秒）、返回的不是 JSON（网关错误页）→ `net.*`，显示「连不上宠物市场，请检查网络」之类。
- 下载接口出错时也是信封，`download.purchase_required` 表示要先买。

## 测试

`tests/AstraPet.Tests/MarketTests.cs`，用假的 `HttpMessageHandler`，不走网络：信封解析、轮询（pending → 通过、slow_down 加间隔、
拒绝 / 过期 / 无效停下、超时、取消）、列表解析和价格、401 清令牌、令牌文件 0600 和退出后删除、安装（成功落到 `{packId}.astrapet`、
覆盖旧版、哈希不对、大小不对、`Inspect` 不通过、格式太新、内置 id、外站下载地址、要购买、下载时 401）。

## 待定

- 服务器的 `nextCursor` 和 `q` / `sort` 绑定；换了排序或搜索词就从头拉。
- 已拥有但下架的宠物：现在列表里看不到，以后可以用 `GET /api/client/library` 做「我的宠物」。
