public static class AgentConfig
{
    public const string ServerUrl = "wss://localhost:9001"; // server address
    public const string Token = "secret"; // authentication token
    public const string ClientId = "agent-1"; // unique client id, e.g., MAC address
    public const bool ValidateServerCertificate = false; // set true to validate TLS cert
}
