using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;

public class PortMap
{
    readonly string _dbPath = Path.Combine(AppContext.BaseDirectory, "portmap.db");

    public PortMap()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS mappings (clientId TEXT PRIMARY KEY, port INTEGER);" +
                          "CREATE TABLE IF NOT EXISTS reserved (port INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }

    public int GetOrAssignPort(string clientId)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT port FROM mappings WHERE clientId=$id";
        cmd.Parameters.AddWithValue("$id", clientId);
        var portObj = cmd.ExecuteScalar();
        if (portObj != null && portObj is long p)
            return (int)p;

        int port = FindFreePort(conn);
        cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO mappings(clientId, port) VALUES($id,$p)";
        cmd.Parameters.AddWithValue("$id", clientId);
        cmd.Parameters.AddWithValue("$p", port);
        cmd.ExecuteNonQuery();
        return port;
    }

    int FindFreePort(SqliteConnection conn)
    {
        for (int p = ServerConfig.PortRangeStart; p <= ServerConfig.PortRangeEnd; p++)
        {
            if (ServerConfig.ReservedPorts.Contains(p)) continue;
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM mappings WHERE port=$p UNION SELECT 1 FROM reserved WHERE port=$p";
            cmd.Parameters.AddWithValue("$p", p);
            if (cmd.ExecuteScalar() == null)
                return p;
        }
        throw new Exception("no free port");
    }

    public IEnumerable<(string ClientId, int Port)> ListMappings()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT clientId, port FROM mappings";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetString(0), reader.GetInt32(1));
        }
    }

    public void AddReserved(int port)
    {
        ServerConfig.ReservedPorts.Add(port);
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO reserved(port) VALUES($p)";
        cmd.Parameters.AddWithValue("$p", port);
        cmd.ExecuteNonQuery();
    }
}
