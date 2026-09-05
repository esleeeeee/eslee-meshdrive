using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;
using Eslee.QuickSend.Core.Transfers;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class FileTransferService(RemoteStorageClient remote, StorageService storage, string dataDirectory) : IAsyncDisposable
{
    private readonly QuickSendCheckpointStore _store = new(Path.Combine(dataDirectory, "transfers"));
    private readonly ConcurrentDictionary<string, TransferProgress> _progress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    public IReadOnlyList<TransferProgress> Progress => _progress.Values.ToArray();
    public string Start(StorageCommand command)
    {
        var id = Guid.NewGuid().ToString("N");
        _progress[id] = new(id, Path.GetFileName(command.Path), 0, 0, "대기", null, null);
        _tasks[id] = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    var result = command.Action == "download" ? await DownloadAsync(command, id, _lifetime.Token).ConfigureAwait(false)
                        : await UploadAsync(command, id, _lifetime.Token).ConfigureAwait(false);
                    _progress[id] = _progress[id] with { State = "완료", Result = result };
                }
                finally { _gate.Release(); }
            }
            catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
            { _progress[id] = _progress[id] with { State = "중단 · 같은 작업으로 재개 가능", Error = e.Message }; }
        });
        return id;
    }
    public async Task WaitAsync(string id) => await _tasks[id].ConfigureAwait(false);
    private void Update(string id, long offset, long total) => _progress[id] = _progress[id] with { State = "복사 중", CompletedBytes = offset, TotalBytes = total };
    private async Task<string> DownloadAsync(StorageCommand command, string id, CancellationToken token)
    {
        var resource = RemoteStorageClient.Resource("manifest", command.ShareId!, command.Path);
        var manifest = await remote.GetAsync<TransferManifest>(command.DeviceId!, resource, token).ConfigureAwait(false); QuickSendAdapter.Validate(manifest);
        var destination = Path.GetFullPath(command.Destination!);
        Directory.CreateDirectory(destination);
        var desired = Path.Combine(destination, Path.GetFileName(command.Path));
        var fileId = QuickSendAdapter.IdFor(command.DeviceId + "|" + resource + "|" + destination + "|" + manifest.Version);
        var record = await RecordAsync(fileId, desired, manifest, token).ConfigureAwait(false);
        if (record.State == TransferState.Completed && File.Exists(record.FinalPath)) return record.FinalPath!;
        await using var receiver = new ReceiverSession(record, _store, new CheckpointPolicy(QuickSendAdapter.ChunkSize));
        await receiver.InitializeAsync(token).ConfigureAwait(false); Update(id, receiver.ReceivedOffset, manifest.Size);
        while (receiver.ReceivedOffset < manifest.Size)
        {
            var path = RemoteStorageClient.Resource("chunk", command.ShareId!, command.Path) + $"&offset={receiver.ReceivedOffset}&fileId={fileId:N}&version={Uri.EscapeDataString(manifest.Version)}";
            using var response = await remote.SendAsync(command.DeviceId!, HttpMethod.Get, path, null, token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (payload.Length <= ProtocolConstants.ChunkMetadataSize || payload.Length > QuickSendAdapter.ChunkSize + ProtocolConstants.ChunkMetadataSize) throw new IOException("올바르지 않은 청크 크기입니다.");
            await receiver.ReceiveChunkAsync(payload, DateTimeOffset.UtcNow, token).ConfigureAwait(false); Update(id, receiver.ReceivedOffset, manifest.Size);
        }
        return await receiver.CompleteAsync(new(fileId, manifest.Size, manifest.LeafCount, manifest.MerkleRoot), DateTimeOffset.UtcNow, token).ConfigureAwait(false);
    }
    private async Task<string> UploadAsync(StorageCommand command, string id, CancellationToken token)
    {
        var manifest = await QuickSendAdapter.ManifestAsync(command.Path, token).ConfigureAwait(false);
        var body = new UploadRequest(command.ShareId!, command.Destination ?? "", Path.GetFileName(command.Path), manifest);
        using var begin = await remote.SendAsync(command.DeviceId!, HttpMethod.Post, "/v1/secure/storage/upload-start", r => r.Content = JsonContent.Create(body), token).ConfigureAwait(false);
        begin.EnsureSuccessStatusCode();
        var ticket = await begin.Content.ReadFromJsonAsync<UploadTicket>(token).ConfigureAwait(false) ?? throw new IOException("업로드 응답이 없습니다.");
        var fileId = Guid.Parse(ticket.Id);
        await using var input = new FileStream(command.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if ($"{input.Length:x}-{File.GetLastWriteTimeUtc(command.Path).Ticks:x}" != manifest.Version) throw new IOException("원본 파일이 변경되었습니다.");
        input.Position = ticket.Offset; var buffer = new byte[QuickSendAdapter.ChunkSize]; Update(id, input.Position, input.Length);
        int count;
        while ((count = await input.ReadAtLeastAsync(buffer, buffer.Length, false, token).ConfigureAwait(false)) > 0)
        {
            var payload = QuickSendAdapter.Pack(fileId, input.Position - count, buffer.AsSpan(0, count));
            using var response = await remote.SendAsync(command.DeviceId!, HttpMethod.Put, $"/v1/secure/storage/upload-chunk?id={ticket.Id}",
                r => r.Content = new ByteArrayContent(payload), token).ConfigureAwait(false); response.EnsureSuccessStatusCode(); Update(id, input.Position, input.Length);
        }
        using var complete = await remote.SendAsync(command.DeviceId!, HttpMethod.Post, $"/v1/secure/storage/upload-complete?id={ticket.Id}", null, token).ConfigureAwait(false);
        complete.EnsureSuccessStatusCode(); return body.Name;
    }
    private async Task<TransferFileRecord> RecordAsync(Guid id, string desired, TransferManifest manifest, CancellationToken token)
    {
        var previous = await _store.FindFileAsync(id, token).ConfigureAwait(false);
        if (previous is not null && (previous.State != TransferState.Completed || File.Exists(previous.FinalPath))) return previous;
        var staging = Path.Combine(dataDirectory, "transfer-parts"); Directory.CreateDirectory(staging);
        var record = new TransferFileRecord(id, id, Path.GetFileName(desired), "", Path.Combine(staging, id.ToString("N") + ".part"), desired,
            manifest.Size, manifest.ModifiedUtcTicks, manifest.Version, manifest.ChunkSize, 0, 0, [], TransferState.Transferring);
        await _store.UpsertFileAsync(record, token).ConfigureAwait(false); return record;
    }
    public async Task<UploadTicket> BeginUploadAsync(string deviceId, UploadRequest request, CancellationToken token)
    {
        QuickSendAdapter.Validate(request.Manifest);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name != Path.GetFileName(request.Name) || request.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || request.Name.EndsWith('.') || request.Name.EndsWith(' ')) throw new UnauthorizedAccessException("올바르지 않은 파일 이름입니다.");
        var parent = storage.Resolve(deviceId, request.ShareId, request.Path, SharePermissions.Upload);
        var desired = Path.Combine(parent, request.Name);
        var id = QuickSendAdapter.IdFor(deviceId + "|" + request.ShareId + "|" + request.Path + "|" + request.Name + "|" + request.Manifest.Version + "|" + request.Manifest.MerkleRoot);
        await _receiveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var record = await RecordAsync(id, desired, request.Manifest, token).ConfigureAwait(false);
            var envelope = new UploadEnvelope(deviceId, request);
            await File.WriteAllTextAsync(UploadPath(id), JsonSerializer.Serialize(envelope), token).ConfigureAwait(false);
            if (record.State == TransferState.Completed) return new(id.ToString("N"), record.Size);
            await using var receiver = new ReceiverSession(record, _store, new CheckpointPolicy(QuickSendAdapter.ChunkSize));
            await receiver.InitializeAsync(token).ConfigureAwait(false); return new(id.ToString("N"), receiver.CommittedOffset);
        }
        finally { _receiveGate.Release(); }
    }
    private string UploadPath(Guid id) => Path.Combine(dataDirectory, "transfers", id.ToString("N") + ".upload");
    public async Task ReceiveUploadAsync(string deviceId, Guid id, byte[]? payload, CancellationToken token)
    {
        await _receiveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<UploadEnvelope>(await File.ReadAllTextAsync(UploadPath(id), token).ConfigureAwait(false)) ?? throw new IOException("업로드 세션이 없습니다.");
            if (envelope.DeviceId != deviceId) throw new UnauthorizedAccessException("다른 기기의 업로드 세션입니다.");
            var parent = storage.Resolve(deviceId, envelope.Request.ShareId, envelope.Request.Path, SharePermissions.Upload);
            var record = await _store.FindFileAsync(id, token).ConfigureAwait(false) ?? throw new IOException("업로드 기록이 없습니다.");
            if (!string.Equals(Path.GetDirectoryName(record.FinalPath), parent, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("공유 폴더가 변경되었습니다.");
            if (record.State == TransferState.Completed) return;
            await using var receiver = new ReceiverSession(record, _store, new CheckpointPolicy(QuickSendAdapter.ChunkSize));
            await receiver.InitializeAsync(token).ConfigureAwait(false);
            if (payload is not null)
            {
                var chunk = new ChunkPayload(payload);
                if (chunk.Length != Math.Min(QuickSendAdapter.ChunkSize, record.Size - chunk.Offset) || chunk.Offset % QuickSendAdapter.ChunkSize != 0) throw new IOException("올바르지 않은 업로드 청크입니다.");
                await receiver.ReceiveChunkAsync(payload, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            }
            else
            {
                var manifest = envelope.Request.Manifest;
                await receiver.CompleteAsync(new(id, manifest.Size, manifest.LeafCount, manifest.MerkleRoot), DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            }
        }
        finally { _receiveGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_tasks.Values).ConfigureAwait(false); _lifetime.Dispose(); _gate.Dispose(); _receiveGate.Dispose();
    }
    private sealed record UploadEnvelope(string DeviceId, UploadRequest Request);
}
