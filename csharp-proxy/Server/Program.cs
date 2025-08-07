using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(ServerConfig.WebSocketPort, listen => listen.UseHttps(ServerConfig.CertPath, ServerConfig.CertPassword));
});
builder.Services.AddSingleton<PortMap>();
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
ConcurrentDictionary<string, AgentConnection> agents = new();

// WebSocket tunnel for agents
app.Map("/tunnel", async (HttpContext ctx, PortMap portMap) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var token = ctx.Request.Query["token"].ToString();
    if (token != ServerConfig.Token)
    {
        ctx.Response.StatusCode = 401;
        return;
    }
    var id = ctx.Request.Query["id"].ToString();
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var agent = new AgentConnection(id, ws, portMap);
    agents[id] = agent;
    await agent.RunAsync();
    agents.TryRemove(id, out _);
});

// HTTP forward endpoint
app.Map("/http", async (HttpContext ctx) =>
{
    var token = ctx.Request.Headers["X-Forward-Token"].ToString();
    var target = ctx.Request.Headers["X-Target-Url"].ToString();
    var method = ctx.Request.Headers["X-Forward-Method"].ToString();
    if (string.IsNullOrEmpty(method)) method = ctx.Request.Method;
    if (!agents.TryGetValue(token, out var agent) || string.IsNullOrEmpty(target))
    {
        ctx.Response.StatusCode = 502;
        return;
    }
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
    var resp = await agent.SendHttpAsync(method, target, headers, Encoding.UTF8.GetBytes(body));
    ctx.Response.StatusCode = resp.StatusCode;
    foreach (var h in resp.Headers)
        ctx.Response.Headers[h.Key] = h.Value;
    await ctx.Response.Body.WriteAsync(resp.Body, 0, resp.Body.Length);
});

// Admin page listing online agents and ports
app.Map("/admin", async (HttpContext ctx, PortMap portMap) =>
{
    if (!Authenticate(ctx)) return;
    var sb = new StringBuilder();
    sb.Append("<html><body><h3>Mappings</h3><ul>");
    foreach (var (cid, port) in portMap.ListMappings())
        sb.Append($"<li>{WebUtility.HtmlEncode(cid)} -> {port}</li>");
    sb.Append("</ul></body></html>");
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync(sb.ToString());
});

// Reserve a port via admin
app.MapPost("/admin/reserve/{port:int}", (int port, HttpContext ctx, PortMap portMap) =>
{
    if (!Authenticate(ctx)) return Results.Unauthorized();
    portMap.AddReserved(port);
    return Results.Ok();
});

bool Authenticate(HttpContext ctx)
{
    string? auth = ctx.Request.Headers["Authorization"];
    if (auth == null || !auth.StartsWith("Basic "))
    {
        ctx.Response.Headers["WWW-Authenticate"] = "Basic";
        ctx.Response.StatusCode = 401;
        return false;
    }
    var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])).Split(':');
    if (pair.Length != 2 || pair[0] != ServerConfig.AdminUser || pair[1] != ServerConfig.AdminPass)
    {
        ctx.Response.StatusCode = 401;
        return false;
    }
    return true;
}

app.Run();

// === supporting types ===
record HttpResp(int StatusCode, Dictionary<string, string> Headers, byte[] Body);

class AgentConnection
{
    readonly string id;
    readonly WebSocket ws;
    readonly PortMap portMap;
    readonly ConcurrentDictionary<string, TaskCompletionSource<HttpResp>> pending = new();
    readonly ConcurrentDictionary<string, TcpClient> forwards = new();

    public AgentConnection(string id, WebSocket ws, PortMap map)
    {
        this.id = id;
        this.ws = ws;
        portMap = map;
    }

    public async Task RunAsync()
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.GetProperty("Type").GetString();
                var mid = doc.RootElement.GetProperty("Id").GetString()!;
                if (type == "HttpResponse")
                {
                    var resp = new HttpResp(
                        doc.RootElement.GetProperty("StatusCode").GetInt32(),
                        doc.RootElement.GetProperty("Headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
                        doc.RootElement.GetProperty("Body").GetBytesFromBase64());
                    if (pending.TryRemove(mid, out var tcs)) tcs.SetResult(resp);
                }
                else if (type == "PortRequest")
                {
                    int port = portMap.GetOrAssignPort(mid);
                    _ = StartPortListener(port);
                    await SendTextAsync(new { Type = "PortAssigned", Id = mid, Port = port });
                }
                else if (type == "PortClose")
                {
                    if (forwards.TryRemove(mid, out var c)) c.Close();
                }
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                var mid = Encoding.ASCII.GetString(buffer, 0, 36);
                if (forwards.TryGetValue(mid, out var c))
                {
                    await c.GetStream().WriteAsync(buffer.AsMemory(36, result.Count - 36));
                }
            }
        }
    }

    public async Task<HttpResp> SendHttpAsync(string method, string url, Dictionary<string, string> headers, byte[] body)
    {
        var id = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<HttpResp>();
        pending[id] = tcs;
        await SendTextAsync(new
        {
            Type = "HttpRequest",
            Id = id,
            Method = method,
            Url = url,
            Headers = headers,
            Body = Convert.ToBase64String(body)
        });
        return await tcs.Task;
    }

    async Task StartPortListener(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        while (ws.State == WebSocketState.Open)
        {
            var remote = await listener.AcceptTcpClientAsync();
            var id = Guid.NewGuid().ToString();
            forwards[id] = remote;
            await SendTextAsync(new { Type = "PortOpen", Id = id, Port = 22 });
            _ = PumpRemoteToAgent(id, remote);
        }
    }

    async Task PumpRemoteToAgent(string id, TcpClient remote)
    {
        var stream = remote.GetStream();
        var buf = new byte[4096 + 36];
        Encoding.ASCII.GetBytes(id, 0, id.Length, buf, 0);
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf, 36, buf.Length - 36);
                if (n <= 0) break;
                await ws.SendAsync(buf.AsMemory(0, n + 36), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        finally
        {
            await SendTextAsync(new { Type = "PortClose", Id = id });
            remote.Close();
            forwards.TryRemove(id, out _);
        }
    }

    Task SendTextAsync(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
