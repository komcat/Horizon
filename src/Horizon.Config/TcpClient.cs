using Horizon.Common;

namespace Horizon.Config;

/// <summary>
/// A TCP data-stream client. <see cref="ProtocolType"/> <c>"single"</c> uses
/// <see cref="DataChannel"/>; <c>"multi"</c> uses <see cref="DataChannels"/>.
/// </summary>
public record TcpClientConfig
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    /// <summary><c>"single"</c> or <c>"multi"</c>.</summary>
    public string ProtocolType { get; init; } = "single";
    /// <summary>Channel name for single-channel mode.</summary>
    public string DataChannel { get; init; } = string.Empty;
    /// <summary>Channel names for multi-channel mode.</summary>
    public List<string> DataChannels { get; init; } = new();
    public bool AutoConnect { get; init; }
    public bool AutoReconnect { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>Loads / saves <c>tcp_clients.json</c>.</summary>
public static class TcpClientJsonLoader
{
    private record TcpClientsFile
    {
        public List<TcpClientConfig> TcpClients { get; init; } = new();
    }

    public static IReadOnlyList<TcpClientConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<TcpClientsFile>(configDir, "tcp_clients")?.TcpClients
               ?? new List<TcpClientConfig>();
    }

    public static void Save(IReadOnlyList<TcpClientConfig> clients, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "tcp_clients",
            new TcpClientsFile { TcpClients = clients.ToList() });
    }
}
