namespace MeshDrive.Protocol;

public sealed record AgentStatus(
    string State,
    int ProcessId,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    int ProtocolVersion,
    string Version,
    string SessionId,
    int ClientCount,
    string DeviceId,
    string DeviceName,
    string Discovery);
