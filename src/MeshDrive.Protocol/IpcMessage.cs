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

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
