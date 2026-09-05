using System.Security.Cryptography;
using System.Text.Json;

namespace MeshDrive.Core;

public sealed record SyncFolder(string Id, string Name, string LocalPath, List<string> AllowedDevices);
public sealed record SyncEntry(string Path, long Size, string Hash);
public sealed record SyncVersion(string Id, string RootId, string Path, string Hash, DateTimeOffset CreatedAt, long Size);

public sealed class SyncFolders
{
    private readonly string _settings;
    private readonly string _versions;
    private readonly string _policy;
    private readonly object _gate = new();
    private readonly List<SyncFolder> _folders;
    public int VersionsPerFile { get; set; } = 20;
    public int RetentionDays { get; set; } = 30;

    public SyncFolders(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _settings = Path.Combine(dataDirectory, "sync-folders.json");
        _versions = Path.Combine(dataDirectory, "sync-versions");
        _policy = Path.Combine(dataDirectory, "sync-retention.json");
        if (File.Exists(_policy))
        {
            var policy = JsonSerializer.Deserialize<Retention>(File.ReadAllText(_policy));
            if (policy is not null) { VersionsPerFile = Math.Clamp(policy.Count, 1, 1000); RetentionDays = Math.Clamp(policy.Days, 1, 3650); }
        }
        Directory.CreateDirectory(_versions);
        _folders = File.Exists(_settings) ? JsonSerializer.Deserialize<List<SyncFolder>>(File.ReadAllText(_settings)) ?? [] : [];
    }
    private sealed record Retention(int Count, int Days);
    public void ConfigureRetention(int count, int days)
    {
        if (count is < 1 or > 1000 || days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(count), "버전은 1~1000개, 기간은 1~3650일입니다.");
        lock (_gate)
        {
            File.WriteAllText(_policy + ".tmp", JsonSerializer.Serialize(new Retention(count, days))); File.Move(_policy + ".tmp", _policy, true);
            VersionsPerFile = count; RetentionDays = days;
        }
    }
    public IReadOnlyList<SyncFolder> Snapshot()
    {
        lock (_gate) return _folders.Select(f => f with { AllowedDevices = [.. f.AllowedDevices] }).ToArray();
    }
    public SyncFolder Save(string? id, string name, string localPath, IEnumerable<string> allowedDevices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(allowedDevices);
        var path = Path.GetFullPath(localPath).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(path, Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("전체 드라이브를 동기화할 수 없습니다.", nameof(localPath));
        SafeSharePath.Resolve(path, "");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException();
        lock (_gate)
        {
            if (id is not null && !_folders.Any(f => f.Id == id)) throw new ArgumentException("동기화 폴더가 없습니다.", nameof(id));
            var folder = new SyncFolder(id ?? Guid.NewGuid().ToString("N"), name.Trim(), path, allowedDevices.Distinct(StringComparer.Ordinal).ToList());
            var next = _folders.Where(f => f.Id != folder.Id).Append(folder).ToList();
            SaveSettings(next); _folders.Clear(); _folders.AddRange(next); return folder;
        }
    }
    public void Remove(string id)
    {
        lock (_gate) { var next = _folders.Where(f => f.Id != id).ToList(); SaveSettings(next); _folders.Clear(); _folders.AddRange(next); }
    }
    private void SaveSettings(List<SyncFolder> folders)
    {
        File.WriteAllText(_settings + ".tmp", JsonSerializer.Serialize(folders)); File.Move(_settings + ".tmp", _settings, true);
    }
    public SyncFolder Require(string id, string? device = null)
    {
        lock (_gate)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == id) ?? throw new UnauthorizedAccessException("동기화가 설정되지 않은 폴더입니다.");
            if (device is not null && !folder.AllowedDevices.Contains(device, StringComparer.Ordinal)) throw new UnauthorizedAccessException("동기화가 허용되지 않은 기기입니다.");
            return folder with { AllowedDevices = [.. folder.AllowedDevices] };
        }
    }
    public static string FileHash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    public string Resolve(string rootId, string relativePath, string? device = null) => SafeSharePath.Resolve(Require(rootId, device).LocalPath, relativePath);
    public IReadOnlyList<SyncEntry> Inventory(string rootId, string? device = null)
    {
        var root = Require(rootId, device).LocalPath;
        var result = new List<SyncEntry>();
        var pending = new Stack<string>(); pending.Push("");
        while (pending.TryPop(out var relative))
        {
            var parent = SafeSharePath.Resolve(root, relative);
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if (Path.GetFileName(entry).StartsWith('.')) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                var path = Path.GetRelativePath(root, entry).Replace('\\', '/');
                var safe = SafeSharePath.Resolve(root, path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else result.Add(new(path, new FileInfo(safe).Length, FileHash(safe)));
                if (result.Count + pending.Count > 100_000) throw new IOException("동기화 폴더의 항목 수가 제한을 넘었습니다.");
            }
        }
        return result;
    }
    public IReadOnlyList<SyncVersion> Versions(string rootId)
    {
        lock (_gate)
        {
            Require(rootId);
            return Directory.EnumerateFiles(_versions, "*.json").Select(f => JsonSerializer.Deserialize<SyncVersion>(File.ReadAllText(f)))
                .Where(v => v is not null && v.RootId == rootId).Cast<SyncVersion>().OrderByDescending(v => v.CreatedAt).ToArray();
        }
    }
    public string? CurrentHash(string rootId, string path, string? device = null)
    {
        var local = WritablePath(rootId, path, device, false);
        return File.Exists(local) ? FileHash(local) : null;
    }
    public void Apply(string rootId, string path, string? expectedHash, string? sourcePath, string? newHash, string? device = null)
    {
        lock (_gate)
        {
            var local = WritablePath(rootId, path, device, sourcePath is not null);
            var current = File.Exists(local) ? FileHash(local) : null;
            if (current != expectedHash) throw new IOException("동기화 중 파일이 변경되었습니다. 다음 검사에서 다시 처리합니다.");
            if (sourcePath is not null && (newHash is null || FileHash(sourcePath) != newHash)) throw new IOException("동기화 파일 무결성 검사 실패");
            if (current == newHash) return;
            if (current is not null) Preserve(rootId, path, local, current);
            if (sourcePath is null)
            {
                if ((File.Exists(local) ? FileHash(local) : null) != expectedHash) throw new IOException("버전 보관 중 원본이 변경되었습니다. 삭제하지 않았습니다.");
                if (File.Exists(local)) File.Delete(local);
            }
            else
            {
                var temp = Path.Combine(Path.GetDirectoryName(local)!, ".meshdrive-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(sourcePath, temp, false);
                    if (FileHash(temp) != newHash) throw new IOException("동기화 임시 파일 무결성 검사 실패");
                    if ((File.Exists(local) ? FileHash(local) : null) != expectedHash) throw new IOException("파일 복사 중 원본이 변경되었습니다. 교체하지 않았습니다.");
                    File.Move(temp, local, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            TrimVersions(rootId, path);
        }
    }
    public void Restore(string rootId, string versionId)
    {
        lock (_gate)
        {
            if (!Guid.TryParseExact(versionId, "N", out _)) throw new ArgumentException("올바르지 않은 버전입니다.", nameof(versionId));
            var version = Versions(rootId).Single(v => v.Id == versionId);
            Apply(rootId, version.Path, CurrentHash(rootId, version.Path), Path.Combine(_versions, version.Id + ".bin"), version.Hash);
        }
    }
    private void Preserve(string rootId, string relativePath, string source, string hash)
    {
        var id = Guid.NewGuid().ToString("N"); var bytes = Path.Combine(_versions, id + ".bin");
        File.Copy(source, bytes, false);
        if (FileHash(bytes) != hash) throw new IOException("이전 버전 보관 실패. 원본을 변경하지 않았습니다.");
        var version = new SyncVersion(id, rootId, relativePath, hash, DateTimeOffset.UtcNow, new FileInfo(bytes).Length);
        File.WriteAllText(Path.Combine(_versions, id + ".json"), JsonSerializer.Serialize(version));
    }
    private void TrimVersions(string rootId, string relativePath)
    {
        var versions = Versions(rootId).Where(v => v.Path == relativePath).ToArray();
        foreach (var item in versions.Skip(Math.Clamp(VersionsPerFile, 1, 1000))) DeleteVersion(item.Id);
        foreach (var item in versions.Skip(1).Take(Math.Clamp(VersionsPerFile, 1, 1000) - 1).Where(v => v.CreatedAt < DateTimeOffset.UtcNow.AddDays(-Math.Clamp(RetentionDays, 1, 3650)))) DeleteVersion(item.Id);
    }
    private void DeleteVersion(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("잘못된 버전 저장소 항목입니다.");
        File.Delete(Path.Combine(_versions, id + ".bin")); File.Delete(Path.Combine(_versions, id + ".json"));
    }
    private string WritablePath(string rootId, string path, string? device, bool createParents)
    {
        var root = Require(rootId, device).LocalPath;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) || path.Contains(':')) throw new UnauthorizedAccessException("동기화 파일 경로가 아닙니다.");
        var parts = path.Split('/'); var parent = SafeSharePath.Resolve(root, "");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new UnauthorizedAccessException("올바르지 않은 동기화 경로입니다.");
        }
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var next = Path.Combine(parent, parts[i]);
            if (!Directory.Exists(next))
            {
                if (!createParents) return Path.Combine(root, Path.Combine(parts));
                Directory.CreateDirectory(next);
            }
            parent = SafeSharePath.Resolve(root, string.Join('/', parts.Take(i + 1)));
        }
        var local = Path.Combine(parent, parts[^1]);
        if (File.Exists(local) || Directory.Exists(local)) SafeSharePath.Resolve(root, path);
        if (Directory.Exists(local)) throw new IOException("동기화 파일과 폴더 이름이 충돌합니다.");
        return local;
    }
}
