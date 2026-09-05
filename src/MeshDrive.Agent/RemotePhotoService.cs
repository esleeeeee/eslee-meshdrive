namespace MeshDrive.Agent;

public sealed class RemotePhotoService(RemoteStorageClient remote, string dataDirectory) : IDisposable
{
    public void Dispose() => _gate.Dispose();
    private readonly PhotoCache _cache = new(Path.Combine(dataDirectory, "photo-cache"));
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string> GetAsync(string deviceId, string shareId, string path, bool thumbnail, CancellationToken cancellationToken)
    {
        if (!PhotoCache.IsImage(path)) throw new IOException("사진 파일을 선택하세요.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resource = RemoteStorageClient.Resource(thumbnail ? "thumbnail" : "content", shareId, path);
            using var head = await remote.SendAsync(deviceId, HttpMethod.Head, resource, null, cancellationToken).ConfigureAwait(false);
            head.EnsureSuccessStatusCode();
            var length = head.Content.Headers.ContentLength ?? throw new IOException("사진 크기를 확인하지 못했습니다.");
            if (length > 256 * 1024 * 1024) throw new IOException("사진 임시 열기는 256 MiB까지 지원합니다. 파일 가져오기를 사용하세요.");
            var key = deviceId + "|" + resource + "|" + head.Headers.ETag + "|" + head.Content.Headers.LastModified + "|" + length;
            var target = _cache.PathFor(key, thumbnail ? ".jpg" : Path.GetExtension(path).ToLowerInvariant());
            if (!File.Exists(target))
            {
                using var response = await remote.SendAsync(deviceId, HttpMethod.Get, resource,
                    request => { if (head.Headers.ETag is { } etag) request.Headers.IfMatch.Add(etag); }, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var temp = target + ".tmp";
                try
                {
                    await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buffer = new byte[65536]; long copied = 0; int count;
                        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            copied += count; if (copied > length) throw new IOException("사진 크기가 변경되었습니다.");
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        }
                        if (copied != length) throw new IOException("사진 전송이 완료되지 않았습니다.");
                    }
                    File.Move(temp, target, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow); _cache.Trim(target); return target;
        }
        finally { _gate.Release(); }
    }
}
