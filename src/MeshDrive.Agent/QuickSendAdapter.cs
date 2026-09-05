using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eslee.QuickSend.Core.Integrity;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;
using Eslee.QuickSend.Core.Transfers;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public static class QuickSendAdapter
{
    public const int ChunkSize = ProtocolConstants.DefaultChunkSize;
    public static async Task<TransferManifest> ManifestAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var modified = File.GetLastWriteTimeUtc(path).Ticks;
        var merkle = new MerkleAccumulator(); var buffer = new byte[ChunkSize]; int count;
        while ((count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, token).ConfigureAwait(false)) > 0) merkle.AddChunk(buffer.AsSpan(0, count));
        return new(stream.Length, modified, $"{stream.Length:x}-{modified:x}", ChunkSize, merkle.LeafCount, Convert.ToBase64String(merkle.ComputeRoot()));
    }
    public static byte[] Pack(Guid id, long offset, ReadOnlySpan<byte> bytes)
    {
        var payload = new byte[ProtocolConstants.ChunkMetadataSize + bytes.Length];
        id.TryWriteBytes(payload, true, out _); BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16), offset);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(24), (uint)bytes.Length); SHA256.HashData(bytes, payload.AsSpan(28, 32));
        bytes.CopyTo(payload.AsSpan(ProtocolConstants.ChunkMetadataSize)); return payload;
    }
    public static Guid IdFor(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    public static async Task VerifyStoredFileAsync(string path, TransferManifest expected, CancellationToken token)
    {
        // ReceiverSession owns the writer and denies other writers while allowing this read.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        var stored = new MerkleAccumulator(); var buffer = new byte[ChunkSize]; int count;
        while ((count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, token).ConfigureAwait(false)) > 0)
            stored.AddChunk(buffer.AsSpan(0, count));
        if (stream.Length != expected.Size || stored.LeafCount != expected.LeafCount ||
            !CryptographicOperations.FixedTimeEquals(stored.ComputeRoot(), Convert.FromBase64String(expected.MerkleRoot)))
            throw new IOException("저장된 파일의 무결성 검사에 실패했습니다. 완료 처리하지 않았습니다.");
    }

    public static async Task<TransferFileRecord> VerifyResumeAsync(TransferFileRecord record, CancellationToken token)
    {
        if (record.CommittedOffset == 0 || record.State == TransferState.Completed) return record;
        long valid = 0;
        var leaves = new MerkleAccumulator();
        if (File.Exists(record.PartialPath))
        {
            await using var input = new FileStream(record.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var buffer = new byte[ChunkSize];
            var limit = Math.Min(record.Size, Math.Min(record.CommittedOffset, input.Length));
            while (valid < limit)
            {
                var count = (int)Math.Min(ChunkSize, record.Size - valid);
                var leaf = checked((int)(valid / ChunkSize));
                if (count > limit - valid || (long)(leaf + 1) * 32 > record.MerkleLeaves.Length) break;
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                var candidate = new MerkleAccumulator(); candidate.AddChunk(buffer.AsSpan(0, count));
                if (!CryptographicOperations.FixedTimeEquals(candidate.ExportLeaves(), record.MerkleLeaves.AsSpan(leaf * 32, 32))) break;
                leaves.AddChunk(buffer.AsSpan(0, count)); valid += count;
            }
        }
        return record with { ReceivedOffset = valid, CommittedOffset = valid, MerkleLeaves = leaves.ExportLeaves() };
    }
    public static void Validate(TransferManifest manifest)
    {
        if (manifest.Size < 0 || manifest.ChunkSize != ChunkSize || manifest.LeafCount != manifest.Size / ChunkSize + (manifest.Size % ChunkSize == 0 ? 0 : 1) || Convert.FromBase64String(manifest.MerkleRoot).Length != 32)
            throw new IOException("올바르지 않은 전송 정보입니다.");
    }
}

public sealed class QuickSendCheckpointStore : ITransferStore
{
    private readonly string _directory;
    public QuickSendCheckpointStore(string directory) { _directory = directory; Directory.CreateDirectory(directory); }
    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");
    public ValueTask UpsertFileAsync(TransferFileRecord file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var path = PathFor(file.FileId);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file)); File.Move(path + ".tmp", path, true); return ValueTask.CompletedTask;
    }
    public ValueTask<TransferFileRecord?> FindFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var path = PathFor(fileId);
        return ValueTask.FromResult(File.Exists(path) ? JsonSerializer.Deserialize<TransferFileRecord>(File.ReadAllText(path)) : null);
    }
    public async ValueTask SaveCheckpointAsync(Guid fileId, long committedOffset, byte[] merkleLeaves, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var record = await FindFileAsync(fileId, cancellationToken).ConfigureAwait(false) ?? throw new IOException("전송 기록이 없습니다.");
        await UpsertFileAsync(record with { ReceivedOffset = committedOffset, CommittedOffset = committedOffset, MerkleLeaves = merkleLeaves }, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask MarkFileCompletedAsync(Guid fileId, string finalPath, byte[] merkleRoot, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var record = await FindFileAsync(fileId, cancellationToken).ConfigureAwait(false) ?? throw new IOException("전송 기록이 없습니다.");
        await UpsertFileAsync(record with { State = TransferState.Completed, FinalPath = finalPath }, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask UpsertJobAsync(TransferJobRecord job, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<TransferJobRecord> FindRecoverableJobsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.CompletedTask.ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); yield break; }
}
