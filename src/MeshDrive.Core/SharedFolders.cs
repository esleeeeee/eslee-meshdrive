using System.Text.Json;

namespace MeshDrive.Core;

[Flags]
public enum SharePermissions { None = 0, Browse = 1, Stream = 2, Download = 4, Upload = 8, ReadOnly = Browse | Stream | Download, All = ReadOnly | Upload }

public sealed record SharedFolder(string Id, string Name, string LocalPath, SharePermissions Permissions,
    Dictionary<string, SharePermissions> DeviceOverrides)
{
    public SharePermissions ForDevice(string deviceId) => DeviceOverrides.GetValueOrDefault(deviceId, Permissions);
}

public sealed record RemoteShare(string Id, string Name, SharePermissions Permissions);
public sealed record RemoteEntry(string Name, string RelativePath, bool IsDirectory, long Length, DateTimeOffset ModifiedAt);

public sealed class SharedFolderStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private List<SharedFolder> _shares;
    public SharedFolderStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _file = Path.Combine(dataDirectory, "shares.json");
        _shares = File.Exists(_file) ? JsonSerializer.Deserialize<List<SharedFolder>>(File.ReadAllText(_file)) ?? [] : [];
    }

    public IReadOnlyList<SharedFolder> Snapshot()
    {
        lock (_gate) return _shares.Select(s => s with { DeviceOverrides = new(s.DeviceOverrides) }).ToArray();
    }

    public SharedFolder Save(string? id, string name, string path, SharePermissions permissions,
        Dictionary<string, SharePermissions>? overrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("공유할 폴더를 찾을 수 없습니다.");
        if ((permissions & ~SharePermissions.All) != 0 || (overrides?.Values.Any(p => (p & ~SharePermissions.All) != 0) ?? false))
            throw new ArgumentException("올바르지 않은 공유 권한입니다.");
        SafeSharePath.Resolve(root, "");
        lock (_gate)
        {
            var share = new SharedFolder(id ?? Guid.NewGuid().ToString("N"), name.Trim(), root, permissions, overrides is null ? [] : new(overrides));
            _shares.RemoveAll(s => s.Id == share.Id);
            _shares.Add(share);
            Persist();
            return share;
        }
    }

    public void Remove(string id) { lock (_gate) { _shares.RemoveAll(s => s.Id == id); Persist(); } }
    public SharedFolder Get(string id) => Snapshot().FirstOrDefault(s => s.Id == id) ?? throw new FileNotFoundException("공유 폴더가 없습니다.");
    private void Persist()
    {
        File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_shares));
        File.Move(_file + ".tmp", _file, true);
    }
}

public static class SafeSharePath
{
    // Reject reparse points, including links inside the share, to keep the access boundary explicit.
    public static string Resolve(string root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':'))
            throw new UnauthorizedAccessException("공유 폴더 밖의 경로는 사용할 수 없습니다.");
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new UnauthorizedAccessException("올바르지 않은 상대 경로입니다.");
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetPathRoot(fullRoot)!;
        foreach (var part in fullRoot[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
                throw new UnauthorizedAccessException("숨김·시스템 파일 또는 링크에 접근할 수 없습니다.");
        }
        return current;
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("링크 또는 junction 폴더는 공유할 수 없습니다.");
    }
}
