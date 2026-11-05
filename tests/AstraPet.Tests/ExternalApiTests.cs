using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AstraPet.Core;
using AstraPet.Core.External;
using AstraPet.External;

namespace AstraPet.Tests;

/// <summary>把 HostRig 包成外部接口能操作的宠物。</summary>
internal sealed class RigPet(HostRig rig) : IExternalPet
{
    public PetDirector Director => rig.Director;
    public PetSettings Settings => rig.Settings;
    public PetEventBus Events => rig.Events;
    public List<(string Text, bool Speak)> Said { get; } = new();
    public void Say(string text, bool speak) => Said.Add((text, speak));
}

public class ExternalApiTests : IDisposable
{
    private const string Token = "secret-token-1234";
    private readonly HostRig _rig = new();
    private readonly RigPet _pet;
    private readonly ExternalApi _api;

    public ExternalApiTests()
    {
        _rig.Settings.ExternalToken = Token;
        _rig.Host.Start();
        _pet = new RigPet(_rig);
        _api = new ExternalApi(_pet, new InlineUi(), _rig.Time);
    }

    public void Dispose() => _rig.Dispose();

    private Task<ExternalResponse> Send(string method, string path, string body = "", string? auth = "Bearer " + Token,
        string? origin = null) => _api.HandleAsync(new ExternalRequest(method, path, auth, origin, body));

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Basic secret-token-1234")]
    public async Task Rejects_missing_or_wrong_token(string? auth)
    {
        var r = await Send("GET", "/v1/status", auth: auth);
        Assert.Equal(401, r.Status);
    }

    [Fact]
    public async Task Empty_token_in_settings_never_authorizes()
    {
        _rig.Settings.ExternalToken = "";
        Assert.Equal(401, (await Send("GET", "/v1/status", auth: "Bearer ")).Status);
    }

    [Fact]
    public async Task Rejects_browser_requests()
    {
        Assert.Equal(403, (await Send("GET", "/v1/status", origin: "https://evil.example")).Status);
    }

    [Fact]
    public async Task Rate_limits_to_five_per_second()
    {
        for (var i = 0; i < ExternalApi.MaxRequestsPerSecond; i++)
            Assert.Equal(200, (await Send("GET", "/v1/status")).Status);
        Assert.Equal(429, (await Send("GET", "/v1/status")).Status);
        _rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(200, (await Send("GET", "/v1/status")).Status);
    }

    [Fact]
    public async Task Status_reports_pet_without_private_data()
    {
        _rig.Settings.Memories.Add(new MemoryItem { Text = "用户喜欢猫" });
        var r = await Send("GET", "/v1/status");
        Assert.Equal(200, r.Status);
        Assert.Equal(_rig.Director.CurrentAction.Id, (string?)r.Body["action"]);
        var json = r.Body.ToJsonString();
        Assert.DoesNotContain("猫", json);
        Assert.DoesNotContain(Token, json);
    }

    [Fact]
    public async Task Lists_enabled_actions()
    {
        var r = await Send("GET", "/v1/actions");
        var ids = r.Body["actions"]!.AsArray().Select(a => (string)a!["id"]!).ToList();
        Assert.Equal(_rig.Director.Library.Actions.All.Where(a => a.Enabled).Select(a => a.Id), ids);
    }

    [Fact]
    public async Task Plays_action_by_id_with_agent_priority()
    {
        var id = _rig.Director.Library.Actions.WithRole(Roles.Click)[0].Id;
        var r = await Send("POST", "/v1/actions/play", $"{{\"id\":\"{id}\"}}");
        Assert.Equal(200, r.Status);
        Assert.Equal("played", (string?)r.Body["result"]);
        Assert.Equal(id, _rig.Director.CurrentAction.Id);
        Assert.Equal(ActionPriority.Agent, _rig.Director.CurrentPriority);
        Assert.Equal("external", _rig.Director.CurrentSource);
    }

    [Fact]
    public async Task Plays_action_by_role_with_fallback_and_loop()
    {
        var r = await Send("POST", "/v1/actions/play", "{\"role\":\"celebrate\",\"loopSeconds\":30}");
        Assert.Equal(200, r.Status);
        Assert.True(_rig.Director.Looping);
    }

    [Theory]
    [InlineData("{\"id\":\"nope\"}", 404)]
    [InlineData("{}", 400)]
    [InlineData("{\"id\":\"a\",\"role\":\"happy\"}", 400)]
    [InlineData("not json", 400)]
    [InlineData("[1]", 400)]
    public async Task Bad_play_requests(string body, int status)
    {
        Assert.Equal(status, (await Send("POST", "/v1/actions/play", body)).Status);
    }

    [Fact]
    public async Task Says_text_with_length_limit()
    {
        Assert.Equal(200, (await Send("POST", "/v1/say", "{\"text\":\"测试全部通过！\",\"speak\":true}")).Status);
        Assert.Equal(("测试全部通过！", true), _pet.Said.Single());

        var tooLong = new string('啊', ExternalApi.MaxSayLength + 1);
        Assert.Equal(400, (await Send("POST", "/v1/say", $"{{\"text\":\"{tooLong}\"}}")).Status);
        Assert.Single(_pet.Said);
    }

    [Fact]
    public async Task Events_go_through_trigger_rules()
    {
        var seen = new List<PetEvent>();
        _rig.Events.Published += seen.Add;

        Assert.Equal(200, (await Send("POST", "/v1/events", "{\"type\":\"external.started\",\"data\":{\"task\":\"build\"}}")).Status);
        Assert.Equal("build", seen.Single()["task"]);
        // 内置规则：完成 → 庆祝（测试角色没有 celebrate，回退到 click），Agent 优先级。
        Assert.Equal(200, (await Send("POST", "/v1/events", "{\"type\":\"external.done\"}")).Status);
        Assert.True(_rig.Director.CurrentAction.HasRole(Roles.Click));
        Assert.Equal(ActionPriority.Agent, _rig.Director.CurrentPriority);
        Assert.Equal("rule:external-done", _rig.Director.CurrentSource);
    }

    [Theory]
    [InlineData("chat.replied")]
    [InlineData("pet.clicked")]
    [InlineData("external.")]
    [InlineData("external.Bad")]
    public async Task Only_external_events_are_accepted(string type)
    {
        var seen = new List<PetEvent>();
        _rig.Events.Published += seen.Add;
        Assert.Equal(400, (await Send("POST", "/v1/events", $"{{\"type\":\"{type}\"}}")).Status);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Unknown_paths_and_methods()
    {
        Assert.Equal(404, (await Send("GET", "/v1/memories")).Status);
        Assert.Equal(405, (await Send("POST", "/v1/status", "{}")).Status);
        Assert.Equal(200, (await Send("GET", "/v1/status/?x=1")).Status);
    }
}

public class LocalHttpServerTests
{
    [Fact]
    public async Task Serves_requests_over_loopback()
    {
        ExternalRequest? seen = null;
        using var server = new LocalHttpServer(r =>
        {
            seen = r;
            return Task.FromResult(ExternalResponse.Ok(new JsonObject { ["ok"] = "好" }));
        });
        server.Start(0);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/say")
        {
            Content = new StringContent("{\"text\":\"你好\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", "t");
        using var response = await http.SendAsync(request);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("{\"ok\":\"好\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(("POST", "/v1/say", "Bearer t", "{\"text\":\"你好\"}"), (seen!.Method, seen.Path, seen.Authorization, seen.Body));
    }

    [Fact]
    public async Task Rejects_oversized_body()
    {
        using var server = new LocalHttpServer(_ => Task.FromResult(ExternalResponse.Ok(new JsonObject())));
        server.Start(0);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var big = new StringContent(new string('x', ExternalApi.MaxBodyBytes + 1));
        try
        {
            using var response = await http.PostAsync("/v1/say", big);
            Assert.Equal(413, (int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // 服务器回 413 后就关连接，客户端可能还没发完请求体：连接被重置也算拒绝了。
        }
    }

    [Fact]
    public void Busy_port_throws()
    {
        using var first = new LocalHttpServer(_ => Task.FromResult(ExternalResponse.Ok(new JsonObject())));
        first.Start(0);
        using var second = new LocalHttpServer(_ => Task.FromResult(ExternalResponse.Ok(new JsonObject())));
        Assert.ThrowsAny<SocketException>(() => second.Start(first.Port));
    }
}

public class PetMcpToolsTests
{
    [Fact]
    public void Defines_the_tools()
    {
        Assert.Equal(["pet_status", "get_pet_status", "pet_list_actions", "pet_play_action", "pet_say"], PetMcpTools.Tools.Select(t => t.Name));
        Assert.All(PetMcpTools.Tools, t => Assert.Equal("object", (string?)t.InputSchema["type"]));
    }

    [Fact]
    public async Task Forwards_calls_and_only_known_arguments()
    {
        var calls = new List<(string, string, string?)>();
        var tools = new PetMcpTools((method, path, body, _) =>
        {
            calls.Add((method, path, body?.ToJsonString()));
            return Task.FromResult<(int, JsonNode?)>((200, new JsonObject { ["result"] = "played" }));
        });

        var (text, isError) = await tools.CallAsync("pet_play_action",
            new JsonObject { ["role"] = "celebrate", ["priority"] = "user" });
        Assert.False(isError);
        Assert.Contains("played", text);
        Assert.Equal(("POST", "/v1/actions/play", "{\"role\":\"celebrate\"}"), calls.Single());
    }

    [Fact]
    public async Task Reports_http_errors_and_unreachable_pet()
    {
        var failing = new PetMcpTools((_, _, _, _) =>
            Task.FromResult<(int, JsonNode?)>((404, new JsonObject { ["error"] = "没有这个动作：x" })));
        Assert.Equal(("没有这个动作：x", true), await failing.CallAsync("pet_play_action", new JsonObject { ["id"] = "x" }));

        var down = new PetMcpTools((_, _, _, _) => throw new HttpRequestException("refused"), "宠物没开");
        Assert.Equal(("宠物没开", true), await down.CallAsync("pet_status", null));

        Assert.True((await down.CallAsync("rm_rf", null)).IsError);
    }

    [Fact]
    public void Reads_port_and_token_from_settings_file()
    {
        var file = Path.Combine(Path.GetTempPath(), "astra-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal((PetSettings.DefaultExternalPort, ""), McpCommand.ReadEndpoint(file));
            File.WriteAllText(file, "{\"ExternalPort\": 50001, \"ExternalToken\": \"abc\", \"Memories\": []}");
            Assert.Equal((50001, "abc"), McpCommand.ReadEndpoint(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
