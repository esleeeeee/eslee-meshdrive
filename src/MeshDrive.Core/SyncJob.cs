namespace MeshDrive.Core;

public sealed record SyncJob(string Id, string LocalRootId, string DeviceId, string RemoteRootId, SyncMode Mode, bool Enabled);
public sealed record SyncJobStatus(string Id, string State, DateTimeOffset? LastRun, int Conflicts, string? Error);
