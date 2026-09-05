using MeshDrive.Agent;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class TransferTests
{
    [TestMethod]
    public async Task CorruptedPartialIsNotCompletedAndResumeRetransmitsDamagedBytes()
    {
        await using var node = await StorageHttpsTests.Node.CreateAsync("A");
        var bytes = new byte[127]; new Random(19).NextBytes(bytes);
        var source = Path.Combine(node.Data, "original.bin"); await File.WriteAllBytesAsync(source, bytes);
        var manifest = await QuickSendAdapter.ManifestAsync(source, CancellationToken.None);
        var share = node.Storage.Shares.Save(null, "Files", node.Root, SharePermissions.All);
        var request = new UploadRequest(share.Id, "", "copy.bin", manifest);
        var ticket = await node.Transfers.BeginUploadAsync("peer", request, CancellationToken.None);
        var id = Guid.Parse(ticket.Id);
        await node.Transfers.ReceiveUploadAsync("peer", id, QuickSendAdapter.Pack(id, 0, bytes), CancellationToken.None);
        var partial = Path.Combine(node.Data, "transfer-parts", ticket.Id + ".part");
        var corrupted = (byte[])bytes.Clone(); corrupted[0] ^= 1; await File.WriteAllBytesAsync(partial, corrupted);
        await Assert.ThrowsExactlyAsync<IOException>(() => node.Transfers.ReceiveUploadAsync("peer", id, null, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(node.Root, "copy.bin")));
        await using var restarted = new FileTransferService(node.Remote, node.Storage, node.Data);
        var resumed = await restarted.BeginUploadAsync("peer", request, CancellationToken.None);
        Assert.AreEqual(0L, resumed.Offset);
        await restarted.ReceiveUploadAsync("peer", id, QuickSendAdapter.Pack(id, 0, bytes), CancellationToken.None);
        await restarted.ReceiveUploadAsync("peer", id, null, CancellationToken.None);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(node.Root, "copy.bin")));
    }
    [TestMethod]
    public async Task QuickSendDownloadUploadAndCollisionPreserveBytes()
    {
        await using var a = await StorageHttpsTests.Node.CreateAsync("A"); await using var b = await StorageHttpsTests.Node.CreateAsync("B"); await a.PairAsync(b);
        var bytes = new byte[QuickSendAdapter.ChunkSize + 127]; new Random(42).NextBytes(bytes);
        var source = Path.Combine(b.Root, "test.bin"); await File.WriteAllBytesAsync(source, bytes);
        var share = b.Storage.Shares.Save(null, "Files", b.Root, SharePermissions.All);
        var existing = Path.Combine(a.Root, "test.bin"); await File.WriteAllTextAsync(existing, "preserved");
        var id = a.Transfers.Start(new() { Action = "download", DeviceId = b.Identity.DeviceId, ShareId = share.Id, Path = "test.bin", Destination = a.Root });
        await a.Transfers.WaitAsync(id);
        var result = a.Transfers.Progress.Single(p => p.Id == id); Assert.AreEqual("완료", result.State, result.Error);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(result.Result!)); Assert.AreEqual("preserved", await File.ReadAllTextAsync(existing));
        var upload = a.Transfers.Start(new() { Action = "upload", DeviceId = b.Identity.DeviceId, ShareId = share.Id, Path = result.Result!, Destination = "" });
        await a.Transfers.WaitAsync(upload); Assert.AreEqual("완료", a.Transfers.Progress.Single(p => p.Id == upload).State);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(b.Root, Path.GetFileName(result.Result!))));
    }
    [TestMethod]
    public async Task UploadResumesCommittedChunkAndRejectsCorruption()
    {
        await using var node = await StorageHttpsTests.Node.CreateAsync("A");
        var bytes = new byte[QuickSendAdapter.ChunkSize + 13]; new Random(12).NextBytes(bytes);
        var source = Path.Combine(node.Data, "source.bin"); await File.WriteAllBytesAsync(source, bytes);
        var manifest = await QuickSendAdapter.ManifestAsync(source, CancellationToken.None);
        var share = node.Storage.Shares.Save(null, "Files", node.Root, SharePermissions.All);
        var request = new UploadRequest(share.Id, "", "copied.bin", manifest);
        var ticket = await node.Transfers.BeginUploadAsync("peer", request, CancellationToken.None);
        var id = Guid.Parse(ticket.Id);
        var chunk = QuickSendAdapter.Pack(id, 0, bytes.AsSpan(0, QuickSendAdapter.ChunkSize));
        chunk[^1] ^= 1;
        await Assert.ThrowsExactlyAsync<Eslee.QuickSend.Core.Transfers.ChunkIntegrityException>(() => node.Transfers.ReceiveUploadAsync("peer", id, chunk, CancellationToken.None));
        chunk[^1] ^= 1; await node.Transfers.ReceiveUploadAsync("peer", id, chunk, CancellationToken.None);
        await using var restarted = new FileTransferService(node.Remote, node.Storage, node.Data);
        var resumed = await restarted.BeginUploadAsync("peer", request, CancellationToken.None); Assert.AreEqual((long)QuickSendAdapter.ChunkSize, resumed.Offset);
        await restarted.ReceiveUploadAsync("peer", id, QuickSendAdapter.Pack(id, resumed.Offset, bytes.AsSpan(QuickSendAdapter.ChunkSize)), CancellationToken.None);
        await restarted.ReceiveUploadAsync("peer", id, null, CancellationToken.None);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(node.Root, "copied.bin")));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => restarted.ReceiveUploadAsync("intruder", id, null, CancellationToken.None));
    }
}
