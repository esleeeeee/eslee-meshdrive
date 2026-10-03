using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class SyncInbox(SyncFolders folders, string dataDirectory, Func<string, long>? availableBytes = null)
{
    public static readonly TimeSpan ResumeRetention = TimeSpan.FromDays(7);
    public const int ChunkSize = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory = Path.Combine(dataDirectory, "sync-inbox");
    private sealed record Envelope(string DeviceId, SyncUploadRequest Request);

    public SyncUploadTicket Begin(string device, SyncUploadRequest request)
    {
        if (request.Size < 0 || request.NewHash.Length != 64 || !request.NewHash.All(char.IsAsciiHexDigit)) throw new ArgumentException("올바르지 않은 동기화 파일 정보입니다.", nameof(request));
        lock (_gate)
        {
            var current = folders.CurrentHash(request.RootId, request.Path, device);
            var envelope = new Envelope(device, request);
            var text = JsonSerializer.Serialize(envelope);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            if (current == request.NewHash) { Remove(id); return new(id, request.Size, true); }
            if (current != request.ExpectedHash) throw new IOException("대상 파일이 변경되었습니다.");
            Directory.CreateDirectory(_directory);
            var reservations = ReadReservations();
            CleanupExpired();
            reservations.RemoveAll(entry => !File.Exists(PathFor(entry.Id, ".json")));
            // Free space already excludes bytes written to partials. Reserve only each
            // other session's remaining bytes, and never evict an admitted resume.
            if (!reservations.Any(entry => entry.Id == id))
            {
                var remaining = Math.Max(0, (availableBytes ?? DiskAvailableBytes)(_directory));
                foreach (var entry in reservations)
                    remaining = Math.Max(0, remaining - Math.Min(remaining, entry.Remaining));
                if (request.Size > remaining) throw new IOException("Sync inbox disk space is reserved by active uploads or unavailable; retry after freeing space.");
            }
            var state = PathFor(id, ".json");
            File.WriteAllText(state + ".tmp", text); File.Move(state + ".tmp", state, true);
            using var partial = new FileStream(PathFor(id, ".part"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var offset = Math.Min(partial.Length, request.Size);
            if (offset != request.Size) offset -= offset % ChunkSize;
            partial.SetLength(offset); partial.Flush(true);
            return new(id, offset, false);
        }
    }
    public void Append(string device, string id, long offset, byte[] bytes)
    {
        lock (_gate)
        {
            var envelope = Load(device, id); var request = envelope.Request;
            if (offset < 0 || offset % ChunkSize != 0 || bytes.Length == 0 || bytes.Length != Math.Min(ChunkSize, request.Size - offset)) throw new IOException("동기화 청크 범위 오류");
            using var partial = new FileStream(PathFor(id, ".part"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (partial.Length != offset) throw new IOException("동기화 재개 위치가 변경되었습니다.");
            partial.Position = offset; partial.Write(bytes); partial.Flush(true);
            File.SetLastWriteTimeUtc(PathFor(id, ".json"), DateTime.UtcNow);
        }
    }
    public void Complete(string device, string id)
    {
        lock (_gate)
        {
            var envelope = Load(device, id); var request = envelope.Request;
            var part = PathFor(id, ".part");
            if (folders.CurrentHash(request.RootId, request.Path, device) == request.NewHash) { Remove(id); return; }
            if (new FileInfo(part).Length != request.Size || SyncFolders.FileHash(part) != request.NewHash)
            {
                using var reset = new FileStream(part, FileMode.Truncate, FileAccess.Write, FileShare.None);
                throw new IOException("동기화 파일 무결성 오류. 재전송이 필요합니다.");
            }
            folders.Apply(request.RootId, request.Path, request.ExpectedHash, part, request.NewHash, device);
            Remove(id);
        }
    }
    private static long DiskAvailableBytes(string directory) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;

    private List<(string Id, long Remaining)> ReadReservations()
    {
        var result = new List<(string Id, long Remaining)>();
        foreach (var state in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(state);
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) continue;
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(state));
                if (envelope?.Request is not { } request || string.IsNullOrWhiteSpace(envelope.DeviceId) ||
                    request.Size < 0 || string.IsNullOrWhiteSpace(request.RootId) || string.IsNullOrWhiteSpace(request.Path) ||
                    request.NewHash is not { Length: 64 } || !request.NewHash.All(char.IsAsciiHexDigit))
                    throw new JsonException("Invalid inbox envelope.");
                var canonicalId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)))).ToLowerInvariant();
                if (!string.Equals(id, canonicalId, StringComparison.OrdinalIgnoreCase)) throw new JsonException("Inbox identity mismatch.");
                var partial = PathFor(id, ".part");
                var stored = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                result.Add((id, Math.Max(0, request.Size - stored)));
            }
            catch (JsonException)
            {
                // Preserve only this application's named state and its matching bytes;
                // unrelated files and healthy sessions are untouched.
                var quarantine = Path.Combine(_directory, "quarantine", id + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(quarantine);
                foreach (var extension in new[] { ".json", ".part", ".json.tmp" })
                {
                    var source = PathFor(id, extension);
                    if (File.Exists(source)) File.Move(source, Path.Combine(quarantine, Path.GetFileName(source)));
                }
            }
        }
        return result;
    }

    private void Remove(string id)
    {
        File.Delete(PathFor(id, ".part"));
        File.Delete(PathFor(id, ".json"));
        File.Delete(PathFor(id, ".json.tmp"));
    }
    private void CleanupExpired()
    {
        if (!Directory.Exists(_directory)) return;
        var cutoff = DateTime.UtcNow - ResumeRetention;
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            var id = name.Split('.')[0];
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) continue;
            var state = PathFor(id, ".json");
            var lastActivity = File.Exists(state) ? File.GetLastWriteTimeUtc(state) : File.GetLastWriteTimeUtc(file);
            if (lastActivity < cutoff) Remove(id);
        }
    }

    private Envelope Load(string device, string id)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(PathFor(id, ".json"))) ?? throw new IOException("동기화 전송이 없습니다.");
        if (envelope.DeviceId != device) throw new UnauthorizedAccessException("다른 기기의 동기화 전송입니다.");
        folders.Require(envelope.Request.RootId, device); return envelope;
    }
    private string PathFor(string id, string extension)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) throw new UnauthorizedAccessException("올바르지 않은 동기화 전송입니다.");
        return Path.Combine(_directory, id + extension);
    }
}
