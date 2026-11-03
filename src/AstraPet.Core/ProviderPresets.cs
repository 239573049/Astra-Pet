namespace AstraPet.Core;

/// <summary>
/// 一个常用提供商的预设：添加提供商时按它预填地址、协议和模型建议。
/// <see cref="Icon"/> 是 LobeHub Icons 的图标名，对应 Assets/providers/{Icon}.png（见 tools/fetch_provider_icons.sh）；
/// 没有图标的（自定义）留空，界面上改画 <see cref="Glyph"/>。
/// <see cref="CatalogId"/> 是它在 models.dev 里的提供商 id：有的话，地址、协议、模型候选以内置的模型库为准
/// （见 <see cref="ModelCatalog.Apply"/>），这里手写的 <see cref="BaseUrl"/> / <see cref="Models"/> 只是兜底
/// 和首选模型；models.dev 没收录的厂商（文心、混元、星火、百川、零一）就只用手写的。
/// 模型名各家常换代，所以表单里还有「获取模型」按钮直接问接口要最新列表。
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string Group,
    string Icon,
    string Accent,
    LlmProtocol Protocol,
    string BaseUrl,
    string[] Models,
    string KeyUrl = "",
    string Hint = "",
    string Glyph = "",
    string[]? Hosts = null,
    string CatalogId = "")
{
    public const string GroupRecommended = "推荐";
    public const string GroupChina = "国内厂商";
    public const string GroupGlobal = "国际厂商";
    public const string GroupLocal = "本地与自定义";

    public const string CustomChat = "custom-chat";
    public const string CustomAnthropic = "custom-anthropic";
}

public static class ProviderPresets
{
    public static readonly IReadOnlyList<ProviderPreset> All =
    [
        // ───── 推荐 ─────
        // 模型网关：一个 Key 通用 Claude、GPT、DeepSeek、Gemini、GLM 等，Chat Completions / Responses / Anthropic Messages
        // 三种协议都有（/v1/chat/completions、/v1/responses、/v1/messages），所以地址带 /v1：Astra 拼 Anthropic 路径时
        // 认 /v1 结尾，换协议不用改地址。模型 id 取自官网模型目录，不在 models.dev 里，所以没有 CatalogId。
        new("nextcowork", "NextCoWork", ProviderPreset.GroupRecommended, "nextcowork", "#B5A68E",
            LlmProtocol.ChatCompletions, "https://nextco.work/v1",
            ["deepseek-v4.1-flash-1017", "claude-sonnet-5-5", "claude-haiku-5-5", "gpt-6-luna", "gpt-6-sol",
             "glm-5.3-flash", "gemini-4-argon", "minimax-m3.1-flash"],
            KeyUrl: "https://nextco.work/dashboard/api-keys",
            Hint: "一个 Key 通用 Claude、GPT、DeepSeek、Gemini、GLM 等模型；三种协议都支持，换协议不用改地址。",
            Hosts: ["nextco.work"]),

        // ───── 国内 ─────
        new("deepseek", "DeepSeek", ProviderPreset.GroupChina, "deepseek", "#4D6BFE",
            LlmProtocol.ChatCompletions, "https://api.deepseek.com",
            ["deepseek-flash", "deepseek-v4-pro"],
            KeyUrl: "https://platform.deepseek.com/api_keys", Hosts: ["api.deepseek.com"], CatalogId: "deepseek"),
        new("qwen", "通义千问 · 百炼", ProviderPreset.GroupChina, "qwen", "#615CED",
            LlmProtocol.ChatCompletions, "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ["qwen-plus", "qwen-max", "qwen-turbo", "qwen-flash"],
            KeyUrl: "https://bailian.console.aliyun.com/?apiKey=1", Hosts: ["dashscope.aliyuncs.com", "dashscope-intl"], CatalogId: "alibaba-cn"),
        new("kimi", "月之暗面 Kimi", ProviderPreset.GroupChina, "kimi", "#1783FF",
            LlmProtocol.ChatCompletions, "https://api.moonshot.cn/v1",
            ["kimi-k2-0905-preview", "kimi-k2-turbo-preview", "moonshot-v1-8k"],
            KeyUrl: "https://platform.moonshot.cn/console/api-keys", Hosts: ["api.moonshot.cn", "api.moonshot.ai"], CatalogId: "moonshotai-cn"),
        new("zhipu", "智谱 GLM", ProviderPreset.GroupChina, "zhipu", "#3859FF",
            LlmProtocol.ChatCompletions, "https://open.bigmodel.cn/api/paas/v4",
            ["glm-4.6", "glm-4.5", "glm-4.5-air"],
            KeyUrl: "https://bigmodel.cn/usercenter/proj-mgmt/apikeys", Hosts: ["open.bigmodel.cn", "api.z.ai"], CatalogId: "zhipuai"),
        new("minimax", "MiniMax", ProviderPreset.GroupChina, "minimax", "#F23F5D",
            LlmProtocol.ChatCompletions, "https://api.minimaxi.com/v1",
            ["MiniMax-M2"],
            KeyUrl: "https://platform.minimaxi.com/user-center/basic-information/interface-key",
            Hosts: ["api.minimaxi.com", "api.minimax.cn", "api.minimax.io", "api.minimax.chat"], CatalogId: "minimax-cn"),
        new("doubao", "火山方舟 · 豆包", ProviderPreset.GroupChina, "doubao", "#2B6BFF",
            LlmProtocol.ChatCompletions, "https://ark.cn-beijing.volces.com/api/v3",
            ["doubao-seed-1-6-250615", "doubao-seed-1-6-flash-250615"],
            KeyUrl: "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey",
            Hint: "模型一栏也可以填方舟控制台里的推理接入点 ID（ep-…）。", Hosts: ["volces.com"], CatalogId: "volcengine"),
        new("wenxin", "百度千帆 · 文心", ProviderPreset.GroupChina, "wenxin", "#2468F2",
            LlmProtocol.ChatCompletions, "https://qianfan.baidubce.com/v2",
            ["ernie-4.5-turbo-128k", "ernie-x1-turbo-32k", "ernie-speed-128k"],
            KeyUrl: "https://console.bce.baidu.com/iam/#/iam/apikey/list", Hosts: ["baidubce.com"]),
        new("hunyuan", "腾讯混元", ProviderPreset.GroupChina, "hunyuan", "#0A6CFF",
            LlmProtocol.ChatCompletions, "https://api.hunyuan.cloud.tencent.com/v1",
            ["hunyuan-turbos-latest", "hunyuan-t1-latest", "hunyuan-lite"],
            KeyUrl: "https://console.cloud.tencent.com/hunyuan/api-key", Hosts: ["hunyuan.cloud.tencent.com"]),
        new("spark", "讯飞星火", ProviderPreset.GroupChina, "spark", "#0070F0",
            LlmProtocol.ChatCompletions, "https://spark-api-open.xf-yun.com/v1",
            ["4.0Ultra", "generalv3.5", "lite"],
            KeyUrl: "https://console.xfyun.cn/services/cbm",
            Hint: "API Key 填控制台「HTTP 服务接口认证信息」里的 APIPassword。", Hosts: ["xf-yun.com"]),
        new("stepfun", "阶跃星辰", ProviderPreset.GroupChina, "stepfun", "#2F6BFF",
            LlmProtocol.ChatCompletions, "https://api.stepfun.com/v1",
            ["step-2-16k", "step-1-8k", "step-1-flash"],
            KeyUrl: "https://platform.stepfun.com/interface-key", Hosts: ["api.stepfun.com"], CatalogId: "stepfun"),
        new("baichuan", "百川智能", ProviderPreset.GroupChina, "baichuan", "#FF9D4D",
            LlmProtocol.ChatCompletions, "https://api.baichuan-ai.com/v1",
            ["Baichuan4-Turbo", "Baichuan4"],
            KeyUrl: "https://platform.baichuan-ai.com/console/apikey", Hosts: ["baichuan-ai.com"]),
        new("yi", "零一万物", ProviderPreset.GroupChina, "yi", "#00A67E",
            LlmProtocol.ChatCompletions, "https://api.lingyiwanwu.com/v1",
            ["yi-lightning", "yi-large"],
            KeyUrl: "https://platform.lingyiwanwu.com/apikeys", Hosts: ["lingyiwanwu.com"]),
        new("siliconflow", "硅基流动", ProviderPreset.GroupChina, "siliconcloud", "#6E29F6",
            LlmProtocol.ChatCompletions, "https://api.siliconflow.cn/v1",
            ["deepseek-ai/DeepSeek-V3", "Qwen/Qwen2.5-72B-Instruct", "THUDM/glm-4-9b-chat"],
            KeyUrl: "https://cloud.siliconflow.cn/account/ak", Hosts: ["siliconflow.cn", "siliconflow.com"], CatalogId: "siliconflow-cn"),
        new("modelscope", "魔搭 ModelScope", ProviderPreset.GroupChina, "modelscope", "#624AFF",
            LlmProtocol.ChatCompletions, "https://api-inference.modelscope.cn/v1",
            ["Qwen/Qwen3-235B-A22B-Instruct-2507", "deepseek-ai/DeepSeek-V3.1"],
            KeyUrl: "https://modelscope.cn/my/myaccesstoken", Hosts: ["modelscope.cn"], CatalogId: "modelscope"),

        // ───── 国际 ─────
        new("openai", "OpenAI", ProviderPreset.GroupGlobal, "openai", "#10A37F",
            LlmProtocol.ChatCompletions, "https://api.openai.com/v1",
            ["gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-4.1-mini"],
            KeyUrl: "https://platform.openai.com/api-keys", Hosts: ["api.openai.com"], CatalogId: "openai"),
        new("openai-responses", "OpenAI Responses", ProviderPreset.GroupGlobal, "openai", "#10A37F",
            LlmProtocol.Responses, "https://api.openai.com/v1",
            ["gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini"],
            KeyUrl: "https://platform.openai.com/api-keys", CatalogId: "openai"),
        new("anthropic", "Anthropic Claude", ProviderPreset.GroupGlobal, "claude", "#D97757",
            LlmProtocol.Anthropic, "https://api.anthropic.com",
            ["claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5"],
            KeyUrl: "https://console.anthropic.com/settings/keys", Hosts: ["api.anthropic.com"], CatalogId: "anthropic"),
        new("gemini", "Google Gemini", ProviderPreset.GroupGlobal, "gemini", "#3186FF",
            LlmProtocol.ChatCompletions, "https://generativelanguage.googleapis.com/v1beta/openai",
            ["gemini-2.5-flash", "gemini-2.5-pro"],
            KeyUrl: "https://aistudio.google.com/apikey", Hint: "走 Gemini 的 OpenAI 兼容接口。",
            Hosts: ["generativelanguage.googleapis.com"], CatalogId: "google"),
        new("grok", "xAI Grok", ProviderPreset.GroupGlobal, "grok", "#9AA3A0",
            LlmProtocol.ChatCompletions, "https://api.x.ai/v1",
            ["grok-4", "grok-3-mini"],
            KeyUrl: "https://console.x.ai", Hosts: ["api.x.ai"], CatalogId: "xai"),
        new("openrouter", "OpenRouter", ProviderPreset.GroupGlobal, "openrouter", "#6566F1",
            LlmProtocol.ChatCompletions, "https://openrouter.ai/api/v1",
            ["openai/gpt-4o-mini", "anthropic/claude-sonnet-4.5", "deepseek/deepseek-chat-v3.1"],
            KeyUrl: "https://openrouter.ai/keys", Hosts: ["openrouter.ai"], CatalogId: "openrouter"),
        new("mistral", "Mistral AI", ProviderPreset.GroupGlobal, "mistral", "#FA520F",
            LlmProtocol.ChatCompletions, "https://api.mistral.ai/v1",
            ["mistral-small-latest", "mistral-large-latest"],
            KeyUrl: "https://console.mistral.ai/api-keys", Hosts: ["api.mistral.ai"], CatalogId: "mistral"),
        new("groq", "Groq", ProviderPreset.GroupGlobal, "groq", "#F55036",
            LlmProtocol.ChatCompletions, "https://api.groq.com/openai/v1",
            ["llama-3.3-70b-versatile", "llama-3.1-8b-instant"],
            KeyUrl: "https://console.groq.com/keys", Hosts: ["api.groq.com"], CatalogId: "groq"),
        new("perplexity", "Perplexity", ProviderPreset.GroupGlobal, "perplexity", "#22B8CD",
            LlmProtocol.ChatCompletions, "https://api.perplexity.ai",
            ["sonar", "sonar-pro"],
            KeyUrl: "https://www.perplexity.ai/settings/api", Hosts: ["api.perplexity.ai"], CatalogId: "perplexity"),

        // ───── 本地与自定义 ─────
        new("ollama", "Ollama", ProviderPreset.GroupLocal, "ollama", "#9AA3A0",
            LlmProtocol.ChatCompletions, "http://localhost:11434/v1",
            ["qwen2.5:7b", "llama3.2"],
            Hint: "本地运行，不需要 API Key。先在终端 ollama pull 下载模型。", Hosts: [":11434"]),
        new("lmstudio", "LM Studio", ProviderPreset.GroupLocal, "lmstudio", "#6B5BFF",
            LlmProtocol.ChatCompletions, "http://localhost:1234/v1",
            [],
            Hint: "在 LM Studio 里启动本地服务器（Developer → Start Server）后，点「获取模型」。", Hosts: [":1234/"]),
        new(ProviderPreset.CustomChat, "自定义 · OpenAI 兼容", ProviderPreset.GroupLocal, "", "#32AA7D",
            LlmProtocol.ChatCompletions, "", [],
            Hint: "中转站、公司网关、自建服务：只要兼容 /chat/completions 就行。", Glyph: "API"),
        new(ProviderPreset.CustomAnthropic, "自定义 · Anthropic 兼容", ProviderPreset.GroupLocal, "", "#D97757",
            LlmProtocol.Anthropic, "", [],
            Hint: "兼容 /v1/messages 的中转站或网关。", Glyph: "API"),
    ];

    public static ProviderPreset? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>按接口地址猜是哪家（导入的配置没有标明来源时用）。猜不到返回 null。</summary>
    public static ProviderPreset? MatchUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var url = baseUrl.Trim().ToLowerInvariant() + "/";
        return All.FirstOrDefault(p => p.Hosts is { } hosts && hosts.Any(url.Contains));
    }

    /// <summary>一个已配置提供商对应的预设：优先看保存的 PresetId，其次按地址猜。</summary>
    public static ProviderPreset? Resolve(LlmProvider provider) =>
        ById(provider.PresetId) ?? MatchUrl(provider.BaseUrl);
}
