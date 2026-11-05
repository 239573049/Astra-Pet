using AstraPet.Core.Behaviors;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AstraPet.Core.External;

/// <summary>外部接口能对宠物做的事。全部在 UI 线程上调用（由 <see cref="ExternalApi"/> 切过去）。</summary>
public interface IExternalPet
{
    PetDirector Director { get; }
    PetSettings Settings { get; }
    PetEventBus Events { get; }
    void Say(string text, bool speak);
    /// <summary>宠物体系；没有或关着时状态里不写 life（只读，外部不能喂食、改数值：design/pet-life/06 §4.3）。</summary>
    PetLife? Life => null;
}

/// <summary>一次 HTTP 请求（已经由服务器解析好）。</summary>
public sealed record ExternalRequest(string Method, string Path, string? Authorization, string? Origin, string Body);

/// <summary>回给外部的结果：状态码 + JSON。</summary>
public sealed record ExternalResponse(int Status, JsonObject Body)
{
    public static ExternalResponse Ok(JsonObject body) => new(200, body);

    public static ExternalResponse Error(int status, string message) =>
        new(status, new JsonObject { ["error"] = message });
}

/// <summary>
/// 本地 HTTP 接口的路由、鉴权和限流（03 §2.1），不碰网络：服务器（Infrastructure 的 LocalHttpServer）收到请求后交给
/// <see cref="HandleAsync"/>，这里校验完再切到 UI 线程操作宠物。
/// 外部请求只能查状态、看动作清单、播动作、说话、发 external.* 事件；读不到对话记录、记忆、设置和密钥。
/// </summary>
public sealed partial class ExternalApi
{
    public const int MaxRequestsPerSecond = 5;
    public const int MaxSayLength = 200;
    public const int MaxBodyBytes = 16 * 1024;
    public const int MaxLoopSeconds = 600;
    public const string EventPrefix = "external.";

    private readonly IExternalPet _pet;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Random _random = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly Lock _lock = new();

    public ExternalApi(IExternalPet pet, IUiDispatcher ui, TimeProvider? time = null)
    {
        _pet = pet;
        _ui = ui;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>当前令牌，每次请求都从设置里读（控制台重新生成后立即生效）。</summary>
    private string Token => _pet.Settings.ExternalToken;

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    [GeneratedRegex(@"^external\.[a-z0-9][a-z0-9.\-]{0,47}$")]
    private static partial Regex EventType();

    public async Task<ExternalResponse> HandleAsync(ExternalRequest request)
    {
        // 浏览器发来的请求一律拒绝：网页不该能控制桌面宠物（令牌之外再挡一层）。
        if (!string.IsNullOrEmpty(request.Origin))
            return ExternalResponse.Error(403, "不接受浏览器的请求");
        if (!Authorized(request.Authorization))
            return ExternalResponse.Error(401, "令牌不对：请求头要带 Authorization: Bearer <令牌>，令牌在控制台「智能 → 外部控制」里复制");
        if (!Admit())
            return ExternalResponse.Error(429, $"请求太频繁，每秒最多 {MaxRequestsPerSecond} 个");

        var path = request.Path.Split('?')[0].TrimEnd('/');
        JsonObject? body = null;
        if (request.Method == "POST")
        {
            if (request.Body.Length > MaxBodyBytes) return ExternalResponse.Error(413, "请求体太大");
            try { body = JsonNode.Parse(request.Body.Length == 0 ? "{}" : request.Body) as JsonObject; }
            catch (JsonException) { }
            if (body is null) return ExternalResponse.Error(400, "请求体要是 JSON 对象");
        }

        return (request.Method, path) switch
        {
            ("GET", "/v1/status") => await OnUi(Status),
            ("GET", "/v1/actions") => await OnUi(Actions),
            ("POST", "/v1/actions/play") => await OnUi(() => Play(body!)),
            ("POST", "/v1/say") => await OnUi(() => Say(body!)),
            ("POST", "/v1/events") => await OnUi(() => Publish(body!)),
            (_, "/v1/status" or "/v1/actions" or "/v1/actions/play" or "/v1/say" or "/v1/events") =>
                ExternalResponse.Error(405, "方法不对"),
            _ => ExternalResponse.Error(404, "没有这个接口"),
        };
    }

    private bool Authorized(string? header)
    {
        var token = Token;
        if (token.Length == 0 || header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var given = header[7..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(token));
    }

    /// <summary>滑动窗口：最近一秒内超过上限就拒绝。</summary>
    private bool Admit()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromSeconds(1)) _recent.Dequeue();
            if (_recent.Count >= MaxRequestsPerSecond) return false;
            _recent.Enqueue(now);
            return true;
        }
    }

    private Task<ExternalResponse> OnUi(Func<ExternalResponse> work)
    {
        var done = new TaskCompletionSource<ExternalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e)
            {
                PetLog.Write($"外部接口出错: {e}");
                done.SetResult(ExternalResponse.Error(500, "宠物这边出错了"));
            }
        });
        return done.Task;
    }

    private ExternalResponse Status()
    {
        var director = _pet.Director;
        var settings = _pet.Settings;
        var body = new JsonObject
        {
            ["pet"] = director.Library.Name,   // 只给名字：用户文件夹角色的 id 是本地路径
            ["action"] = director.CurrentAction.Id,
            ["priority"] = director.CurrentPriority.ToString().ToLowerInvariant(),
            ["paused"] = settings.Paused,
            ["visible"] = settings.Visible,
            ["powerSaving"] = settings.PowerSavingActive,
            ["version"] = AppVersion.Current,
            ["stance"] = director.Stance,
        };
        if (_pet.Life is { Enabled: true } life)
            body["life"] = new JsonObject
            {
                ["satiety"] = Math.Round(life.Data.Satiety),
                ["energy"] = Math.Round(life.Data.Energy),
                ["mood"] = Math.Round(life.Data.Mood),
                ["bondLevel"] = life.Level,
                ["bondName"] = LifeRules.LevelName(life.Level),
                ["daysTogether"] = life.DaysTogether,
            };
        return ExternalResponse.Ok(body);
    }

    private ExternalResponse Actions()
    {
        var list = new JsonArray();
        foreach (var a in _pet.Director.Library.Actions.All.Where(a => a.Enabled))
        {
            var roles = new JsonArray();
            foreach (var r in a.Roles) roles.Add((JsonNode)JsonValue.Create(r)!);
            list.Add((JsonNode)new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.DisplayName,
                ["description"] = a.Description,
                ["roles"] = roles,
            });
        }
        return ExternalResponse.Ok(new JsonObject { ["actions"] = list });
    }

    private ExternalResponse Play(JsonObject body)
    {
        var id = Str(body, "id");
        var role = Str(body, "role");
        if (id is null == (role is null))
            return ExternalResponse.Error(400, "要给 id 或 role 中的一个");

        var set = _pet.Director.Library.Actions;
        var action = id is not null ? set.Find(id) : set.Resolve("role:" + role, _random);
        if (action is null || !action.Enabled)
            return ExternalResponse.Error(404, id is not null ? $"没有这个动作：{id}" : $"没有这个角色的动作：{role}");

        TimeSpan? loop = null;
        if (body["loopSeconds"] is JsonValue lv && lv.TryGetValue<double>(out var seconds) && seconds > 0)
            loop = TimeSpan.FromSeconds(Math.Min(seconds, MaxLoopSeconds));

        var result = _pet.Director.Request(new ActionRequest(action, ActionPriority.Agent, "external", LoopFor: loop));
        return ExternalResponse.Ok(new JsonObject
        {
            ["action"] = action.Id,
            ["result"] = result.ToString().ToLowerInvariant(),
        });
    }

    private ExternalResponse Say(JsonObject body)
    {
        var text = Str(body, "text");
        if (text is null) return ExternalResponse.Error(400, "要给 text");
        if (text.Length > MaxSayLength) return ExternalResponse.Error(400, $"text 最多 {MaxSayLength} 字");
        var speak = body["speak"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
        _pet.Say(text, speak);
        return ExternalResponse.Ok(new JsonObject { ["ok"] = true });
    }

    private ExternalResponse Publish(JsonObject body)
    {
        var type = Str(body, "type");
        if (type is null || !EventType().IsMatch(type))
            return ExternalResponse.Error(400, "type 要以 external. 开头，后面是小写字母、数字、点和短横线，例如 external.task-done");

        var data = new List<(string, string)>();
        if (body["data"] is JsonObject d)
            foreach (var (key, value) in d.Take(8))
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && key.Length <= 32)
                    data.Add((key, s.Length > 200 ? s[..200] : s));
        _pet.Events.Publish(type, [.. data]);
        return ExternalResponse.Ok(new JsonObject { ["ok"] = true });
    }

    private static string? Str(JsonObject body, string key) =>
        body[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
