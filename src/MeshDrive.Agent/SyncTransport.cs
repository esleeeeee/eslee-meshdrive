using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class SyncTransport(RemoteStorageClient remote, string dataDirectory)
{
    public static string Resource(string kind, string rootId, string path = "") => $"/v1/secure/sync/{kind}?rootId={Uri.EscapeDataString(rootId)}&path={Uri.EscapeDataString(path)}";
    public Task<List<SyncEntry>> InventoryAsync(string device, string rootId, CancellationToken token) => remote.GetAsync<List<SyncEntry>>(device, Resource("inventory", rootId), token);
    public async Task<string> DownloadAsync(string device, string rootId, SyncEntry entry, CancellationToken token, Action? checkAccess = null)
    {
        if (entry.Size < 0 || entry.Hash.Length != 64 || !entry.Hash.All(char.IsAsciiHexDigit)) throw new IOException("올바르지 않은 동기화 목록입니다.");
        var directory = Path.Combine(dataDirectory, "sync-downloads"); Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device + "|" + rootId + "|" + entry.Path + "|" + entry.Hash)));
        var part = Path.Combine(directory, key + ".part");
        if (File.Exists(part) && new FileInfo(part).Length == entry.Size && SyncFolders.FileHash(part) == entry.Hash) return part;
        await using (var output = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, true))
        {
            var offset = output.Length > entry.Size || output.Length == entry.Size ? 0 : output.Length - output.Length % SyncInbox.ChunkSize;
            output.SetLength(offset); output.Position = offset;
            while (offset < entry.Size)
            {
                checkAccess?.Invoke();
                var count = (int)Math.Min(SyncInbox.ChunkSize, entry.Size - offset);
                using var response = await remote.SendAsync(device, HttpMethod.Get, Resource("content", rootId, entry.Path) + "&hash=" + Uri.EscapeDataString(entry.Hash),
                    r => r.Headers.Range = new(offset, offset + count - 1), token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentLength != count || response.Content.Headers.ContentRange?.From != offset)
                    throw new IOException("동기화 원본이 변경되었거나 범위 응답이 올바르지 않습니다.");
                var bytes = new byte[count];
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                await output.WriteAsync(bytes, token).ConfigureAwait(false); output.Flush(true); offset += count;
            }
        }
        if (SyncFolders.FileHash(part) != entry.Hash)
        {
            using var reset = new FileStream(part, FileMode.Truncate, FileAccess.Write, FileShare.None);
            throw new IOException("동기화 다운로드 무결성 오류. 다음 시도에서 재전송합니다.");
        }
        return part;
    }
    public async Task UploadAsync(string device, string rootId, string path, string? expected, string source, string hash, CancellationToken token, Action? checkAccess = null)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var begin = await remote.SendAsync(device, HttpMethod.Post, "/v1/secure/sync/upload-start", r => r.Content = JsonContent.Create(new SyncUploadRequest(rootId, path, expected, hash, input.Length)), token).ConfigureAwait(false);
        begin.EnsureSuccessStatusCode();
        var ticket = await begin.Content.ReadFromJsonAsync<SyncUploadTicket>(token).ConfigureAwait(false) ?? throw new IOException("동기화 전송 응답이 없습니다.");
        if (ticket.Completed) return;
        if (ticket.Offset < 0 || ticket.Offset > input.Length || (ticket.Offset != input.Length && ticket.Offset % SyncInbox.ChunkSize != 0)) throw new IOException("잘못된 재개 위치입니다.");
        input.Position = ticket.Offset;
        while (input.Position < input.Length)
        {
            checkAccess?.Invoke();
            var offset = input.Position; var bytes = new byte[(int)Math.Min(SyncInbox.ChunkSize, input.Length - offset)];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            using var response = await remote.SendAsync(device, HttpMethod.Put, $"/v1/secure/sync/upload-chunk?id={Uri.EscapeDataString(ticket.Id)}&offset={offset}", r => r.Content = new ByteArrayContent(bytes), token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        checkAccess?.Invoke();
        using var complete = await remote.SendAsync(device, HttpMethod.Post, "/v1/secure/sync/upload-complete?id=" + Uri.EscapeDataString(ticket.Id), null, token).ConfigureAwait(false);
        complete.EnsureSuccessStatusCode();
    }
    public async Task DeleteAsync(string device, string rootId, string path, string expected, CancellationToken token)
    {
        using var response = await remote.SendAsync(device, HttpMethod.Post, "/v1/secure/sync/delete", r => r.Content = JsonContent.Create(new SyncDeleteRequest(rootId, path, expected)), token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
    public void Release(string path)
    {
        var full = Path.GetFullPath(path); var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.Combine(dataDirectory, "sync-downloads")), StringComparison.OrdinalIgnoreCase) ||
            name.Length != 69 || !name.EndsWith(".part", StringComparison.Ordinal) || !name[..64].All(char.IsAsciiHexDigit))
            throw new ArgumentException("동기화 임시 파일이 아닙니다.", nameof(path));
        File.Delete(full);
    }
}
