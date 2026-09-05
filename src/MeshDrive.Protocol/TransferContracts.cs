namespace MeshDrive.Protocol;

public sealed record TransferManifest(long Size, long ModifiedUtcTicks, string Version, int ChunkSize, int LeafCount, string MerkleRoot);
public sealed record UploadRequest(string ShareId, string Path, string Name, TransferManifest Manifest);
public sealed record UploadTicket(string Id, long Offset);
public sealed record TransferProgress(string Id, string Name, long CompletedBytes, long TotalBytes, string State, string? Result, string? Error);
public sealed record CopyGrantRequest(string TargetDeviceId, string ShareId, string Path);
public sealed record CopyGrant(string RequesterId, string TargetDeviceId, string ShareId, string Path);
public sealed record CopyReceiveRequest(string SourceDeviceId, string Token, string ShareId, string Path);
public sealed record CopyTicket(string Token);
public sealed record CopyJob(string Id);
