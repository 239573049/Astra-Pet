# 05 图片与视频模型提供商

## 1. 提供商模型

和对话模型的 `LlmProvider` 分开存（字段和用途不同，混在一个列表里界面会乱），但界面风格一致（卡片 + 表单 + 测试连接）。

```csharp
public enum MediaKind { Image, Video }

public enum MediaProtocol
{
    ArkVideoTasks,      // 火山方舟 contents/generations/tasks（视频，默认且第一期唯一）
    OpenAIImages,       // /v1/images/generations + /v1/images/edits
    ArkImages,          // 火山方舟 Seedream /api/v3/images/generations
    // 预留：GeminiImage、KlingVideo、ViduVideo …（§2.4）
}

public sealed class MediaProvider : Observable
{
    public string Id { get; set; }
    public string Name { get; set; }
    public MediaKind Kind { get; set; }
    public MediaProtocol Protocol { get; set; }
    public string BaseUrl { get; set; }
    public string ApiKey { get; set; }
    public string Model { get; set; }
    public string ExtraJson { get; set; } = "";       // 高级：原样合并进请求体的 JSON（各家的私有参数）
    public PriceTable Prices { get; set; } = new();   // 用户可编辑的单价，用于估算
}
```

设置里加 `MediaProviders`（列表）、`ActiveImageProviderId`、`ActiveVideoProviderId`。控制台「智能」页加一个「生成模型」分区，工作台第 ⓪ 步复用同一个表单。

预设（像 `ProviderPresets` 一样预填地址、协议、模型建议，模型名常换代，所以都可以改）：

| 预设 | 种类 | 协议 | 地址 |
|---|---|---|---|
| NextCoWork Seedance 2.5（默认推荐） | 视频 | ArkVideoTasks | `https://nextco.work/api/v3` |
| NextCoWork GPT Image 2.5（默认推荐，`gpt-image-2.5-flare`） | 图片 | OpenAIImages | `https://nextco.work/v1` |
| 火山方舟 Seedance | 视频 | ArkVideoTasks | `https://ark.cn-beijing.volces.com/api/v3` |
| BytePlus ModelArk Seedance（海外） | 视频 | ArkVideoTasks | 以 BytePlus 文档为准 |
| 兼容方舟协议的网关 / 自定义 | 视频 | ArkVideoTasks | 用户填 |
| 火山方舟 Seedream | 图片 | ArkImages | `https://ark.cn-beijing.volces.com/api/v3` |
| OpenAI 兼容图片接口（`gpt-image-2.5`、各类网关） | 图片 | OpenAIImages | 用户填，比如 `https://api.openai.com/v1` |

NextCoWork 和对话模型的 NextCoWork 预设是同一个 Key：添加这两个预设时，如果「智能」页里已经有同一域名的对话提供商并填了 Key，直接带过来。
接口路径来自它的文档（`/docs/gateway/images`）和模型页的接入示例（视频协议编号 10 → `/api/v3/contents/generations/tasks`）；
它按 token 计费，预设里的单价是按 2026-11 模型目录折后价粗算的人民币，界面照样写「估算」。

**目前工作台写死用 NextCoWork**：⓪ 只填一个 Key（图片和视频共用，没填时沿用「智能」页里的 NextCoWork Key），地址、协议每次打开按预设改回去；
其他预设留在代码里，以后再开放。视频模型可以在四个 Seedance 里选：

| 模型 | 分辨率 | 每秒参考价（元） |
|---|---|---|
| `doubao-seedance-2-5-260628`（默认） | 480p / 720p / 1080p | 0.63 / 1.40 / 3.45 |
| `doubao-seedance-2-0-260128` | 480p / 720p / 1080p | 0.41 / 0.92 / 2.29 |
| `doubao-seedance-2-0-fast-260128` | 480p / 720p | 0.19 / 0.41 |
| `doubao-seedance-2-0-mini-260615` | 480p / 720p | 0.12 / 0.26 |

默认用模型能出的最高分辨率：新工程和换模型时 ④ 的分辨率设成最高一档，Fast / Mini 的 1080p 选不了；
提交和估价时再按模型收一次（`MediaPresets.FitResolution`），不会把 1080p 发给只支持 720p 的模型。

## 2. 视频：火山方舟任务协议

需求明确要求视频生成「必须使用火山 Seedance 视频接口的协议格式，用户可以配置其他提供商」。理解为：**协议固定为方舟任务协议，
地址、密钥、模型可配**——任何实现了这个协议的服务（方舟本身、BytePlus、转发网关、自建代理）都能接。

### 2.1 协议（按 `tools/petgen/generate.py` 已跑通的用法整理，实现前对照官方文档再核一遍）

**提交任务**

```
POST {BaseUrl}/contents/generations/tasks
Authorization: Bearer {ApiKey}
Content-Type: application/json

{
  "model": "doubao-seedance-2-5-260628",
  "content": [
    { "type": "text", "text": "<公共前缀 + 动作提示词>" },
    { "type": "image_url", "image_url": { "url": "data:image/png;base64,..." }, "role": "first_frame" },
    { "type": "image_url", "image_url": { "url": "data:image/png;base64,..." }, "role": "last_frame" }
  ],
  "ratio": "adaptive",
  "resolution": "720p",
  "duration": 10,
  "generate_audio": false,
  "watermark": false
}
→ 200 { "id": "cgt-2026…" }
```

**查询任务**

```
GET {BaseUrl}/contents/generations/tasks/{id}
→ { "id": "...", "status": "queued|running|succeeded|failed|cancelled|expired",
    "content": { "video_url": "https://..." },       // succeeded 时；链接约 24 小时有效
    "usage": { "completion_tokens": 216900 },
    "error": { "code": "...", "message": "..." } }   // failed 时
```

**取消任务**（排队中的任务）：`DELETE {BaseUrl}/contents/generations/tasks/{id}`——以文档为准，不支持时界面上隐藏「取消」。

注意事项：

- 首帧、首尾帧、参考图三种模式不能混用（`generate.py` 注释里的结论），所以首尾帧模式下不传设定图。
- 图片用 base64 data URL 传，避免要求用户有公网图床。请求前检查图片字节数是否超过协议上限（上限以文档为准，超了先压缩成高质量 JPEG 或缩小）。
- 首尾帧图本身是 16:9（1280×720），`ratio: adaptive` 时输出也是 16:9。
- `ExtraJson` 合并进请求体，用来传 `seed`、`camera_fixed` 等各家扩展参数，或新版本加的字段，不用等程序更新。
- 时长和分辨率的可选值随模型变化，界面上给常用值，同时允许手填；提交失败时原样显示服务端的错误信息。

### 2.2 客户端接口

```csharp
// Core/Studio/Ports
public sealed record VideoJob(
    string Prompt, byte[] FirstFrame, byte[]? LastFrame,
    string Resolution, int DurationSeconds, string Ratio = "adaptive");

public sealed record VideoTaskStatus(
    string TaskId, VideoTaskState State, string? VideoUrl, long? UsageTokens, string? Error);

public enum VideoTaskState { Queued, Running, Succeeded, Failed, Cancelled, Expired }

public interface IVideoGenerator
{
    VideoCapabilities Capabilities { get; }        // 支持首尾帧？可选时长 / 分辨率？能取消？
    Task<string> SubmitAsync(MediaProvider p, VideoJob job, CancellationToken ct);
    Task<VideoTaskStatus> GetAsync(MediaProvider p, string taskId, CancellationToken ct);
    Task CancelAsync(MediaProvider p, string taskId, CancellationToken ct);
}
```

`ArkVideoTaskClient : IVideoGenerator` 在 Infrastructure；下载视频走通用的 `HttpClient`，先写 `.part` 再改名。

### 2.3 任务调度（工作台 ⑤）

- `VideoTaskQueue`（Infrastructure）：并发上限、指数退避轮询（10s 起，最长 30s），HTTP 429 / 5xx 退避重试 **查询**，但 **提交** 失败不自动重试（避免重复计费，交给用户点）。
- 任务记录先落盘（`take-NNN.task.json`），再开始轮询；程序启动时扫描工程里所有未完成的任务记录，恢复轮询。
- 任务成功但下载失败（链接过期）：标记「已生成但未下载」，提示用户这一段需要重新生成。

### 2.4 其他视频协议【待定 D8】

| 方案 | 说明 |
|---|---|
| **A. 只实现方舟任务协议** | 满足需求；接其他家靠兼容网关。推荐第一期 |
| B. 每家一个适配器 | 可灵、Vidu、通义万相、Runway、Veo 等各自的协议。很多家的「首尾帧」支持程度、时长、价格都不同 |

接口已经按 B 预留：新增一个 `MediaProtocol` 值 + 一个 `IVideoGenerator` 实现即可。选择适配器的唯一硬性要求：**支持首尾帧（或至少首帧）**。

## 3. 图片【待定 D9】

### 3.1 需要的能力

| 步骤 | 能力 | 必需 |
|---|---|---|
| ② 设定图（有参考图） | 参考图 + 文字 → 图（图像编辑 / 多图参考） | 有参考图时必需 |
| ② 设定图（无参考图） | 文字 → 图 | ✅ |
| ③ 站姿 | 参考图（设定图）+ 文字 → 图 | ✅ |
| 分段动作关键帧 | 参考图（站姿）+ 文字 → 图，严格保持角色和位置 | 第二期 |
| 透明背景 | 有更好，没有也行 | ❌ —— 我们本来就让模型画纯色抠像底，自己抠 |

### 3.2 协议

**OpenAI Images**（`make_figure.py` 已跑通，经由兼容网关调用 GPT Image）

```
POST {BaseUrl}/images/edits          multipart/form-data
  model, prompt, image[]（可多张）, size, quality, n, output_format=png, background（可选）
POST {BaseUrl}/images/generations    application/json（无参考图时）
→ { "data": [ { "b64_json": "..." } | { "url": "..." } ], "usage": {...} }
```

**火山方舟 Seedream**

```
POST {BaseUrl}/images/generations    application/json
  { "model": "...", "prompt": "...", "image": ["data:image/png;base64,..."] /* 参考图，可多张 */,
    "size": "...", "response_format": "b64_json", "watermark": false }
```

字段名、多图参考的上限、尺寸取值以方舟文档为准，实现时核对。

**Gemini 图像**（`generateContent` 返回图片）放到以后。

### 3.3 客户端接口

```csharp
public sealed record ImageJob(string Prompt, IReadOnlyList<byte[]> References, string Size, int Count, string? Quality);

public interface IImageGenerator
{
    ImageCapabilities Capabilities { get; }   // 支持参考图？最多几张？可选尺寸？支持透明？
    Task<IReadOnlyList<byte[]>> GenerateAsync(MediaProvider p, ImageJob job, CancellationToken ct);
}
```

图片调用通常几十秒内同步返回，不需要任务队列；超时设长一点（600s，参照 `make_figure.py`）。

## 4. 密钥、价格和账本

- **密钥【待定 D15】**：现在 LLM 的密钥明文存在设置文件里（`LlmProvider.ApiKey`）。生成模型的密钥按同样方式存，保持一致。
  更好的做法是在平台层加 `ISecretStore`（macOS 钥匙串 / Windows 凭据管理器 / Linux Secret Service，不支持时退回文件），
  一次性把对话和生成模型的密钥都迁过去——这是独立的改进，建议单独排期，不和工作台绑在一起。
- 密钥只出现在请求头里；任务记录、日志、账本里一律不写密钥，图片 base64 也截断（`generate.py` 的 `redacted`）。
- **价格表**：每个提供商一张用户可编辑的价格表（视频：元 / 秒，按分辨率；图片：元 / 张，按质量）。
  预设里带一个参考值并注明来源和日期，界面上永远写「估算」。不内置远程价格源。
- **账本**：每个工程的 `ledger.jsonl`，一行一次付费调用：`时间、步骤、提供商、模型、参数摘要、估算金额、usage`。工作台首页汇总每个工程的估算花费。

## 5. 网络

- 用 .NET 的 `HttpClient`，证书走系统信任库；`petnet.py` 那个「python.org 的 Python 没有 CA 证书」的问题在 C# 里不存在。
- 代理：沿用程序现有的网络设置（如有），否则读系统代理。
- 测试连接：视频提供商不能用「提交一个任务」来测（要花钱），只做一次 `GET tasks/<不存在的 id>`，根据返回 401 / 404 判断密钥和地址是否正确。
  图片提供商同理，用一个必然失败但不计费的请求（比如缺参数）看鉴权是否通过；实在区分不了就只检查地址可达。
