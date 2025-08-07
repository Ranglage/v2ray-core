using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static ConcurrentDictionary<string, AgentConnection> agents = new();

    static async Task Main(string[] args)
    {
        _ = AcceptAgents();
        _ = StartHttp();
        _ = StartPortForward();
        Console.WriteLine("Server running. Press Enter to exit.");
        Console.ReadLine();
    }

    static async Task AcceptAgents()
    {
        var listener = new TcpListener(IPAddress.Any, 9000);
        listener.Start();
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleAgent(client);
        }
    }

    static async Task HandleAgent(TcpClient client)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var line = await reader.ReadLineAsync();
        if (line == null || !line.StartsWith("AUTH "))
        {
            client.Close();
            return;
        }
        var token = line.Substring(5).Trim();
        var agent = new AgentConnection(token, client, reader, writer);
        agents[token] = agent;
        Console.WriteLine($"Agent {token} connected");
        await agent.ProcessMessages();
        agents.TryRemove(token, out _);
        Console.WriteLine($"Agent {token} disconnected");
    }

    static async Task StartHttp()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://*:8080/");
        listener.Start();
        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() => HandleHttp(ctx));
        }
    }

    static async Task HandleHttp(HttpListenerContext ctx)
    {
        var token = ctx.Request.Headers["X-Forward-Token"];
        var target = ctx.Request.Headers["X-Target-Url"];
        var method = ctx.Request.Headers["X-Forward-Method"] ?? ctx.Request.HttpMethod;
        if (token == null || target == null || !agents.TryGetValue(token, out var agent))
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.Close();
            return;
        }
        string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        var id = Guid.NewGuid().ToString();
        var req = new
        {
            Type = "HttpRequest",
            Id = id,
            Method = method,
            Url = target,
            Headers = ctx.Request.Headers.AllKeys.ToDictionary(k => k!, k => ctx.Request.Headers[k]! ),
            Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(body))
        };
        var tcs = new TaskCompletionSource<HttpResponse>();
        agent.Pending[id] = tcs;
        await agent.SendAsync(req);
        var resp = await tcs.Task;
        ctx.Response.StatusCode = resp.StatusCode;
        foreach (var h in resp.Headers)
            ctx.Response.Headers[h.Key] = h.Value;
        var respBody = Encoding.UTF8.GetBytes(resp.Body);
        await ctx.Response.OutputStream.WriteAsync(respBody, 0, respBody.Length);
        ctx.Response.Close();
    }

    static async Task StartPortForward()
    {
        var listener = new TcpListener(IPAddress.Any, 1212);
        listener.Start();
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleForward(client);
        }
    }

    static async Task HandleForward(TcpClient remote)
    {
        string token = "secret";
        if (!agents.TryGetValue(token, out var agent))
        {
            remote.Close();
            return;
        }
        var id = Guid.NewGuid().ToString();
        agent.ForwardConnections[id] = remote;
        await agent.SendAsync(new { Type = "PortOpen", Id = id, Port = 22 });
        _ = PumpRemoteToAgent(agent, id, remote);
    }

    static async Task PumpRemoteToAgent(AgentConnection agent, string id, TcpClient remote)
    {
        var stream = remote.GetStream();
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                await agent.SendAsync(new { Type = "PortData", Id = id, Data = Convert.ToBase64String(buf, 0, n) });
            }
        }
        finally
        {
            await agent.SendAsync(new { Type = "PortClose", Id = id });
            remote.Close();
        }
    }
}

record HttpResponse(int StatusCode, Dictionary<string, string> Headers, string Body);

class AgentConnection
{
    public string Token { get; }
    public TcpClient Client { get; }
    readonly StreamReader reader;
    readonly StreamWriter writer;
    public ConcurrentDictionary<string, TaskCompletionSource<HttpResponse>> Pending { get; } = new();
    public ConcurrentDictionary<string, TcpClient> ForwardConnections { get; } = new();

    public AgentConnection(string token, TcpClient client, StreamReader r, StreamWriter w)
    {
        Token = token;
        Client = client;
        reader = r;
        writer = w;
    }

    public Task SendAsync(object obj)
    {
        string json = JsonSerializer.Serialize(obj);
        return writer.WriteLineAsync(json);
    }

    public async Task ProcessMessages()
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("Type").GetString();
            var id = doc.RootElement.GetProperty("Id").GetString()!;
            if (type == "HttpResponse")
            {
                var resp = new HttpResponse(
                    doc.RootElement.GetProperty("StatusCode").GetInt32(),
                    doc.RootElement.GetProperty("Headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
                    Encoding.UTF8.GetString(Convert.FromBase64String(doc.RootElement.GetProperty("Body").GetString() ?? ""))
                );
                if (Pending.TryRemove(id, out var tcs)) tcs.SetResult(resp);
            }
            else if (type == "PortData")
            {
                if (ForwardConnections.TryGetValue(id, out var conn))
                {
                    var data = Convert.FromBase64String(doc.RootElement.GetProperty("Data").GetString()!);
                    await conn.GetStream().WriteAsync(data, 0, data.Length);
                }
            }
            else if (type == "PortClose")
            {
                if (ForwardConnections.TryRemove(id, out var conn))
                {
                    conn.Close();
                }
            }
        }
    }
}
