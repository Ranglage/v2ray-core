using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "localhost";
        var client = new TcpClient();
        await client.ConnectAsync(host, 9000);
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("AUTH secret");
        var agent = new Agent(reader, writer);
        await agent.Run();
    }
}

class Agent
{
    readonly StreamReader reader;
    readonly StreamWriter writer;
    readonly HttpClient http = new HttpClient();
    ConcurrentDictionary<string, TcpClient> forwards = new();

    public Agent(StreamReader r, StreamWriter w)
    {
        reader = r;
        writer = w;
    }

    public async Task Run()
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("Type").GetString();
            var id = doc.RootElement.GetProperty("Id").GetString()!;
            if (type == "HttpRequest")
            {
                var method = doc.RootElement.GetProperty("Method").GetString()!;
                var url = doc.RootElement.GetProperty("Url").GetString()!;
                var headers = doc.RootElement.GetProperty("Headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
                var bodyBase64 = doc.RootElement.GetProperty("Body").GetString();
                var body = Encoding.UTF8.GetString(Convert.FromBase64String(bodyBase64 ?? ""));
                var req = new HttpRequestMessage(new HttpMethod(method), url);
                foreach (var h in headers)
                    req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (body.Length > 0)
                    req.Content = new StringContent(body, Encoding.UTF8);
                var resp = await http.SendAsync(req);
                var respHeaders = resp.Headers.Concat(resp.Content.Headers)
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
                var respBody = await resp.Content.ReadAsStringAsync();
                await SendAsync(new
                {
                    Type = "HttpResponse",
                    Id = id,
                    StatusCode = (int)resp.StatusCode,
                    Headers = respHeaders,
                    Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(respBody))
                });
            }
            else if (type == "PortOpen")
            {
                int port = doc.RootElement.GetProperty("Port").GetInt32();
                var local = new TcpClient();
                await local.ConnectAsync("127.0.0.1", port);
                forwards[id] = local;
                _ = PumpLocalToServer(id, local);
            }
            else if (type == "PortData")
            {
                if (forwards.TryGetValue(id, out var local))
                {
                    var data = Convert.FromBase64String(doc.RootElement.GetProperty("Data").GetString()!);
                    await local.GetStream().WriteAsync(data, 0, data.Length);
                }
            }
            else if (type == "PortClose")
            {
                if (forwards.TryRemove(id, out var local))
                {
                    local.Close();
                }
            }
        }
    }

    async Task PumpLocalToServer(string id, TcpClient local)
    {
        var stream = local.GetStream();
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                await SendAsync(new { Type = "PortData", Id = id, Data = Convert.ToBase64String(buf, 0, n) });
            }
        }
        finally
        {
            await SendAsync(new { Type = "PortClose", Id = id });
            local.Close();
        }
    }

    Task SendAsync(object obj)
    {
        string json = JsonSerializer.Serialize(obj);
        return writer.WriteLineAsync(json);
    }
}
