namespace MeshDrive.Protocol;

public sealed record RemoteSyncFolder(string Id, string Name);
public sealed record SyncUploadRequest(string RootId, string Path, string? ExpectedHash, string NewHash, long Size);
public sealed record SyncUploadTicket(string Id, long Offset, bool Completed);
public sealed record SyncDeleteRequest(string RootId, string Path, string ExpectedHash);
public sealed record SyncState(IReadOnlyList<MeshDrive.Core.SyncFolder> Folders, IReadOnlyList<MeshDrive.Core.SyncJob> Jobs,
    IReadOnlyList<MeshDrive.Core.SyncJobStatus> Status, int VersionCount, int RetentionDays);
