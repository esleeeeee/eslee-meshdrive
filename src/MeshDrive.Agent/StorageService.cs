using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed class StorageService(SharedFolderStore shares)
{
    public SharedFolderStore Shares { get; } = shares;
    public bool Paused { get; set; }
    public IReadOnlyList<RemoteShare> ListShares(string deviceId)
    {
        EnsureActive();
        return Shares.Snapshot().Where(s => s.ForDevice(deviceId).HasFlag(SharePermissions.Browse))
            .Select(s => new RemoteShare(s.Id, s.Name, s.ForDevice(deviceId))).ToArray();
    }
    public string Resolve(string deviceId, string shareId, string path, SharePermissions permission)
    {
        EnsureActive();
        var share = Shares.Get(shareId);
        if (!share.ForDevice(deviceId).HasFlag(permission)) throw new UnauthorizedAccessException("이 공유 폴더의 작업 권한이 없습니다.");
        return SafeSharePath.Resolve(share.LocalPath, path);
    }
    public IReadOnlyList<RemoteEntry> ListEntries(string deviceId, string shareId, string path)
    {
        var local = Resolve(deviceId, shareId, path, SharePermissions.Browse);
        return new DirectoryInfo(local).EnumerateFileSystemInfos()
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
            .Select(f => new RemoteEntry(f.Name, string.IsNullOrEmpty(path) ? f.Name : path.TrimEnd('/') + "/" + f.Name,
                f is DirectoryInfo, f is FileInfo file ? file.Length : 0, f.LastWriteTimeUtc))
            .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private void EnsureActive() { if (Paused) throw new UnauthorizedAccessException("상대 기기의 공유가 일시 중지되어 있습니다."); }
}
