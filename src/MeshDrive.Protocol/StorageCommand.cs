using MeshDrive.Core;

namespace MeshDrive.Protocol;

public sealed class StorageCommand
{
    public string Action { get; set; } = "local-shares";
    public string? DeviceId { get; set; }
    public string? ShareId { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Destination { get; set; }
    public SharePermissions Permissions { get; set; } = SharePermissions.ReadOnly;
    public Dictionary<string, SharePermissions>? DeviceOverrides { get; set; }
    public string? TargetDeviceId { get; set; }
    public string? TargetShareId { get; set; }
    public string? CopyToken { get; set; }
    public string? RequesterId { get; set; }
    public List<string>? AllowedDevices { get; set; }
    public SyncJob? SyncJob { get; set; }
    public int VersionCount { get; set; } = 20;
    public int RetentionDays { get; set; } = 30;
}

public sealed class StorageReply
{
    public List<SharedFolder>? LocalShares { get; set; }
    public List<RemoteShare>? Shares { get; set; }
    public List<RemoteEntry>? Entries { get; set; }
    public string? Value { get; set; }
    public List<TransferProgress>? Transfers { get; set; }
}
