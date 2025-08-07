# C# Reverse Proxy

This prototype demonstrates a minimal reverse proxy and port forwarding system inspired by the v2ray-core architecture.

* **Agent (A)** – runs inside the private network and keeps a persistent TCP connection to the server.
* **Server (S)** – accepts the agent connection and exposes:
  * an HTTP endpoint for authenticated clients to issue arbitrary HTTP requests through the agent;
  * a TCP port forward (similar to frp) allowing remote SSH access.

The code focuses on network proxying and removes the other features found in v2ray. Messages are JSON lines exchanged over the persistent connection.

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
   dotnet run --project Agent/Agent.csproj -- <server_host>
   ```
3. Issue HTTP requests through the server:
   ```bash
   curl -H 'X-Forward-Token: secret' -H 'X-Target-Url: http://example.com' \
        -H 'X-Forward-Method: GET' http://<server_host>:8080/proxy
   ```
4. Connect to the forwarded SSH port (`1212`) on the server to reach the agent's local port `22`.

This is a simplified educational example and lacks production-ready security and error handling.
