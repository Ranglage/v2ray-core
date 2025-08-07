using System.Collections.Generic;

public static class ServerConfig
{
    public const int WebSocketPort = 9001; // wss port
    public const string Token = "secret"; // shared token
    public const string CertPath = "server.pfx"; // TLS certificate (PFX)
    public const string CertPassword = "password"; // TLS certificate password
    public const string AdminUser = "admin";
    public const string AdminPass = "adminpass";
    public static readonly HashSet<int> ReservedPorts = new() { WebSocketPort }; // ports that cannot be used
    public const int PortRangeStart = 20000;
    public const int PortRangeEnd = 21000;
}
