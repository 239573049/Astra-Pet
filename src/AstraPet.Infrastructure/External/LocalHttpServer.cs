using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;
using AstraPet.Core.External;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Net.Http.Headers;

namespace AstraPet.External;

/// <summary>
/// 只监听 127.0.0.1 的极简 HTTP/1.1 服务器，把请求交给 <see cref="ExternalApi"/>。
/// 不用 HttpListener：Windows 上它走 http.sys，绑 127.0.0.1 要管理员注册 URL；也不用 Kestrel（NativeAOT 体积大）。
/// 每个连接只处理一个请求（Connection: close），请求头最多 8 KB、请求体最多 16 KB，5 秒读不完就断开。
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 8 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Readable =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<ExternalRequest, Task<ExternalResponse>> _handle;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public LocalHttpServer(Func<ExternalRequest, Task<ExternalResponse>> handle) => _handle = handle;

    public int Port { get; private set; }
    public bool Running => _listener is not null;

    /// <summary>开始监听；端口被占用时抛 <see cref="SocketException"/>。传 0 由系统挑端口（测试用）。</summary>
    public void Start(int port)
    {
        Stop();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }   // 停止监听
            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(ReadTimeout);
            var stream = client.GetStream();
            ExternalResponse response;
            try
            {
                var request = await ReadRequestAsync(stream, timeout.Token);
                response = request is null
                    ? ExternalResponse.Error(400, "请求格式不对")
                    : await _handle(request);
            }
            catch (RequestTooLargeException)
            {
                response = ExternalResponse.Error(413, "请求太大");
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException)
            {
                return;
            }
            try
            {
                await WriteResponseAsync(stream, response, stopping);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
        }
    }

    private sealed class RequestTooLargeException : Exception;

    /// <summary>解析请求行、请求头和按 Content-Length 的请求体。不支持 chunked。</summary>
    internal static async Task<ExternalRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes + ExternalApi.MaxBodyBytes];
        var length = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length >= MaxHeaderBytes) throw new RequestTooLargeException();
            var n = await stream.ReadAsync(buffer.AsMemory(length, MaxHeaderBytes - length), ct);
            if (n == 0) return null;
            length += n;
            headerEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.")) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (headers.ContainsKey("Transfer-Encoding")) return null;

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, out contentLength) || contentLength < 0))
            return null;
        if (contentLength > ExternalApi.MaxBodyBytes) throw new RequestTooLargeException();

        var bodyStart = headerEnd + 4;
        var needed = bodyStart + contentLength;
        while (length < needed)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length, needed - length), ct);
            if (n == 0) return null;
            length += n;
        }

        return new ExternalRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1],
            headers.GetValueOrDefault("Authorization"),
            headers.GetValueOrDefault("Origin"),
            Encoding.UTF8.GetString(buffer, bodyStart, contentLength));
    }

    private static async Task WriteResponseAsync(Stream stream, ExternalResponse response, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(response.Body.ToJsonString(Readable));
        var head =
            $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        _ => "Error",
    };
}

/// <summary>
/// <c>AstraPet mcp</c>：用官方 MCP SDK（ModelContextProtocol.Core）在 stdio 上提供 <see cref="PetMcpTools"/> 的工具，
/// 从设置文件读端口和令牌，把调用转发给正在运行的宠物。只用 SDK 的底层 handler，不用基于反射的特性发现，NativeAOT 下可用。
/// </summary>
public static class McpCommand
{
    public static async Task<int> RunAsync(string settingsFile, CancellationToken ct = default)
    {
        var port = ReadEndpoint(settingsFile).Port;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };

        async Task<(int, JsonNode?)> Send(string method, string path, JsonObject? body, CancellationToken token)
        {
            // 每次重新读令牌：用户在控制台重新生成后不用重启 MCP 客户端。
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadEndpoint(settingsFile).Token);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, token);
            var text = await response.Content.ReadAsStringAsync(token);
            JsonNode? node = null;
            try { node = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
            return ((int)response.StatusCode, node);
        }

        var tools = new PetMcpTools(Send,
            "连不上桌面宠物：请确认 Astra Pet 正在运行，并在控制台「智能 → 外部控制」里打开了本地接口。");
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "astrapet", Title = "Astra Pet", Version = AppVersion.Current },
            ServerInstructions = PetMcpTools.Instructions,
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = PetMcpTools.Tools.Select(t => new Tool
                    {
                        Name = t.Name,
                        Description = t.Description,
                        InputSchema = ToElement(t.InputSchema),
                    }).ToList(),
                }),
                CallToolHandler = async (request, token) =>
                {
                    var p = request.Params;
                    JsonObject? args = null;
                    if (p?.Arguments is { } a)
                    {
                        args = new JsonObject();
                        foreach (var (key, value) in a) args[key] = JsonNode.Parse(value.GetRawText());
                    }
                    var (text, isError) = await tools.CallAsync(p?.Name ?? "", args, token);
                    return new CallToolResult { Content = [new TextContentBlock { Text = text }], IsError = isError };
                },
            },
        };

        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        await server.RunAsync(ct);
        return 0;
    }

    private static JsonElement ToElement(JsonObject o)
    {
        using var doc = JsonDocument.Parse(o.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>只读设置文件里的两个字段，不走 SettingsStore（不迁移、不回写）。</summary>
    internal static (int Port, string Token) ReadEndpoint(string settingsFile)
    {
        try
        {
            if (File.Exists(settingsFile) && JsonNode.Parse(File.ReadAllText(settingsFile)) is JsonObject o)
            {
                var port = o[nameof(PetSettings.ExternalPort)] is JsonValue p && p.TryGetValue<int>(out var v) ? v : PetSettings.DefaultExternalPort;
                var token = o[nameof(PetSettings.ExternalToken)] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "";
                return (port, token);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return (PetSettings.DefaultExternalPort, "");
    }
}
