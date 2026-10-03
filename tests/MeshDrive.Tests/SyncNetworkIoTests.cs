using System.Security.Cryptography;
using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class SyncNetworkIoTests
{
    [TestMethod]
    public async Task RealHttpsInterruptedUploadRestartsAndPreservesReplacedVersion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var sender = await StorageHttpsTests.Node.CreateAsync("Isolated QA sender");
        await using var receiver = await StorageHttpsTests.Node.CreateAsync("Isolated QA receiver");
        await sender.PairAsync(receiver);
        var root = receiver.Sync.Save(null, "QA sync", receiver.Root, [sender.Identity.DeviceId]);
        var original = Path.Combine(receiver.Root, "stream.bin");
        await File.WriteAllTextAsync(original, "original retained as version", token);
        var oldHash = SyncFolders.FileHash(original);
        var source = Path.Combine(sender.Root, "source.bin");
        var block = new byte[SyncInbox.ChunkSize]; new Random(193).NextBytes(block);
        await using (var file = File.Create(source))
        {
            for (var i = 0; i < 16; i++) { block[0] = (byte)i; await file.WriteAsync(block, token); }
            file.Flush(true);
        }
        var hash = SyncFolders.FileHash(source);
        var transport = new SyncTransport(sender.Remote, sender.Data);
        var boundary = 0;
        await Assert.ThrowsExactlyAsync<IOException>(() => transport.UploadAsync(receiver.Identity.DeviceId, root.Id,
            "stream.bin", oldHash, source, hash, token, () => { if (++boundary == 2) throw new IOException("Simulated sender disconnect after one acknowledged HTTPS chunk"); }));
        var staging = Path.Combine(receiver.Data, "sync-inbox");
        var partial = Directory.GetFiles(staging, "*.part").Single();
        Assert.AreEqual((long)SyncInbox.ChunkSize, new FileInfo(partial).Length);
        Assert.AreEqual(oldHash, SyncFolders.FileHash(original));
        await receiver.RestartSyncHostAsync();
        // A new server and inbox instance loads the persisted checkpoint and trust.
        await transport.UploadAsync(receiver.Identity.DeviceId, root.Id, "stream.bin", oldHash, source, hash, token);
        Assert.AreEqual(hash, SyncFolders.FileHash(original));
        Assert.AreEqual(128L * 1024 * 1024, new FileInfo(original).Length);
        Assert.IsEmpty(Directory.GetFiles(staging));
        var previous = receiver.Sync.Versions(root.Id).Single(); Assert.AreEqual(oldHash, previous.Hash);
        var entry = (await transport.InventoryAsync(receiver.Identity.DeviceId, root.Id, token)).Single();
        var downloaded = await transport.DownloadAsync(receiver.Identity.DeviceId, root.Id, entry, token);
        Assert.AreEqual(hash, SyncFolders.FileHash(downloaded));
        transport.Release(downloaded);
        receiver.Sync.Restore(root.Id, previous.Id);
        Assert.AreEqual(oldHash, SyncFolders.FileHash(original));
        Assert.AreEqual("original retained as version", await File.ReadAllTextAsync(original, token));
    }
}
