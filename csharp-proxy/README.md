# C# Reverse Proxy

This prototype demonstrates a minimal reverse proxy and port forwarding system inspired by the v2ray-core architecture.

* **Agent (A)** – runs inside the private network and keeps a persistent WebSocket (wss) connection to the server, using ping/pong as heartbeat.
* **Server (S)** – accepts the agent connection and exposes:
  * an HTTPS endpoint for authenticated clients to issue arbitrary HTTP requests through the agent;
  * dynamic TCP port forwarding (similar to frp) allowing remote SSH access. Ports are assigned per-agent and recorded in SQLite.

Binary WebSocket frames carry raw TCP data without base64 overhead. Control messages and HTTP payloads use JSON text frames.

### Building
A .NET SDK is required:
```bash
dotnet build
```

### Running
1. Start the server:
```bash
dotnet run --project Server/Server.csproj
```
2. Start the agent on the machine that owns the network connection:
```bash
dotnet run --project Agent/Agent.csproj
```
3. Issue HTTP requests through the server:
   ```bash
curl -H 'X-Forward-Token: secret' -H 'X-Target-Url: http://example.com' \
       -H 'X-Forward-Method: GET' https://<server_host>:9001/http
```
4. On first connection the agent requests a public port; check `https://<server_host>:9001/admin` for the assigned value and connect to that port to reach the agent's local port `22`.

This is a simplified educational example and lacks production-ready security and error handling.
