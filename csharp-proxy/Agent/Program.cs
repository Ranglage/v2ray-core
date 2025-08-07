using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        if (!AgentConfig.ValidateServerCertificate)
        {
            ws.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
        }
        var uri = new Uri($"{AgentConfig.ServerUrl}/tunnel?token={AgentConfig.Token}&id={AgentConfig.ClientId}");
        await ws.ConnectAsync(uri, CancellationToken.None);
        var agent = new Agent(ws);
        await agent.Run();
    }
}

class Agent
{
    readonly ClientWebSocket ws;
    readonly HttpClient http = new HttpClient();
    readonly ConcurrentDictionary<string, TcpClient> forwards = new();

    public Agent(ClientWebSocket socket)
    {
        ws = socket;
    }

    public async Task Run()
    {
        await SendTextAsync(new { Type = "PortRequest", Id = AgentConfig.ClientId });
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
                var id = doc.RootElement.GetProperty("Id").GetString()!;
                if (type == "HttpRequest")
                {
                    var method = doc.RootElement.GetProperty("Method").GetString()!;
                    var url = doc.RootElement.GetProperty("Url").GetString()!;
                    var headers = doc.RootElement.GetProperty("Headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
                    var bodyBytes = doc.RootElement.GetProperty("Body").GetBytesFromBase64();
                    var req = new HttpRequestMessage(new HttpMethod(method), url);
                    foreach (var h in headers)
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    if (bodyBytes.Length > 0)
                        req.Content = new ByteArrayContent(bodyBytes);
                    var resp = await http.SendAsync(req);
                    var respHeaders = resp.Headers.Concat(resp.Content.Headers)
                        .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
                    var respBody = await resp.Content.ReadAsByteArrayAsync();
                    await SendTextAsync(new
                    {
                        Type = "HttpResponse",
                        Id = id,
                        StatusCode = (int)resp.StatusCode,
                        Headers = respHeaders,
                        Body = Convert.ToBase64String(respBody)
                    });
                }
                else if (type == "PortAssigned")
                {
                    // server replied with assigned public port, nothing to do client-side
                }
                else if (type == "PortOpen")
                {
                    int port = doc.RootElement.GetProperty("Port").GetInt32();
                    var local = new TcpClient();
                    await local.ConnectAsync("127.0.0.1", port);
                    forwards[id] = local;
                    _ = PumpLocalToServer(id, local);
                }
                else if (type == "PortClose")
                {
                    if (forwards.TryRemove(id, out var local))
                    {
                        local.Close();
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                var id = Encoding.ASCII.GetString(buffer, 0, 36);
                if (forwards.TryGetValue(id, out var local))
                {
                    await local.GetStream().WriteAsync(buffer.AsMemory(36, result.Count - 36));
                }
            }
        }
    }

    async Task PumpLocalToServer(string id, TcpClient local)
    {
        var stream = local.GetStream();
        var buf = ArrayPool<byte>.Shared.Rent(4096 + 36);
        try
        {
            Encoding.ASCII.GetBytes(id, 0, id.Length, buf, 0);
            while (true)
            {
                int n = await stream.ReadAsync(buf, 36, buf.Length - 36);
                if (n <= 0) break;
                await ws.SendAsync(buf.AsMemory(0, n + 36), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            await SendTextAsync(new { Type = "PortClose", Id = id });
            local.Close();
        }
    }

    Task SendTextAsync(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
