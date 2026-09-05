using System.Text.Json.Serialization;

namespace MeshDrive.Protocol;

public sealed class IpcMessage
{
    public StorageCommand? Storage { get; set; }
    public StorageReply? StorageResult { get; set; }
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

    [JsonPropertyName("trusted")]
    public List<IpcTrustedPeer>? Trusted { get; set; }

    [JsonPropertyName("sas")]
    public string? Sas { get; set; }

    [JsonPropertyName("accepted")]
    public bool? Accepted { get; set; }

    [JsonPropertyName("pairingStatus")]
    public string? PairingStatus { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("ipv4")]
    public string? Ipv4 { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("succeeded")]
    public bool? Succeeded { get; set; }

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

    [JsonPropertyName("trustState")]
    public string? TrustState { get; set; }

    [JsonPropertyName("fallbackIpv4s")]
    public List<string>? FallbackIpv4s { get; set; }
}

public sealed class IpcTrustedPeer
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("pairedAt")]
    public DateTimeOffset? PairedAt { get; set; }
}
