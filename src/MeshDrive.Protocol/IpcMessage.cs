using System.Text.Json.Serialization;

namespace MeshDrive.Protocol;

public sealed class IpcMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int? ProtocolVersion { get; set; }

    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("clientKind")]
    public string? ClientKind { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public long? UptimeSeconds { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("clientCount")]
    public int? ClientCount { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("discovery")]
    public string? Discovery { get; set; }

    [JsonPropertyName("peers")]
    public List<IpcPeer>? Peers { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class IpcPeer
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ipv4")]
    public string? Ipv4 { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("online")]
    public bool? Online { get; set; }

    [JsonPropertyName("lastSeen")]
    public DateTimeOffset? LastSeen { get; set; }
}
