using System.Net;
using MeshDrive.Agent;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class SyncHttpsTests
{
    [TestMethod]
    public async Task SyncTransfersChangedBytesAndArchivesDeletionOnlyForOptedInRoots()
    {
        await using var a = await StorageHttpsTests.Node.CreateAsync("A");
        await using var b = await StorageHttpsTests.Node.CreateAsync("B"); await a.PairAsync(b);
        var root = b.Sync.Save(null, "Explicit sync", b.Root, [a.Identity.DeviceId]);
        var transport = new SyncTransport(a.Remote, a.Data);
        var source = Path.Combine(a.Root, "source.bin");
        var bytes = new byte[SyncInbox.ChunkSize + 127]; new Random(92).NextBytes(bytes); await File.WriteAllBytesAsync(source, bytes);
        var hash = SyncFolders.FileHash(source);
        await transport.UploadAsync(b.Identity.DeviceId, root.Id, "nested/file.bin", null, source, hash, CancellationToken.None);
        var inventory = await transport.InventoryAsync(b.Identity.DeviceId, root.Id, CancellationToken.None);
        Assert.AreEqual(hash, inventory.Single().Hash);
        var downloaded = await transport.DownloadAsync(b.Identity.DeviceId, root.Id, inventory.Single(), CancellationToken.None);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(downloaded));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => transport.DeleteAsync(b.Identity.DeviceId, root.Id, "nested/file.bin", "stale", CancellationToken.None));
        await transport.DeleteAsync(b.Identity.DeviceId, root.Id, "nested/file.bin", hash, CancellationToken.None);
        Assert.IsFalse(File.Exists(Path.Combine(b.Root, "nested", "file.bin")));
        Assert.AreEqual(hash, b.Sync.Versions(root.Id).Single().Hash);
        var ordinary = b.Storage.Shares.Save(null, "Ordinary", b.Root, SharePermissions.All);
        using var denied = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get, SyncTransport.Resource("inventory", ordinary.Id), null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        b.Sync.Save(root.Id, root.Name, root.LocalPath, []);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => transport.InventoryAsync(b.Identity.DeviceId, root.Id, CancellationToken.None));
    }

    [TestMethod]
    public void SyncInboxSurvivesRestartAndRejectsDamagedCompletion()
    {
        var data = Path.Combine(Path.GetTempPath(), "meshdrive-sync-inbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var rootPath = Path.Combine(data, "root"); Directory.CreateDirectory(rootPath);
            var folders = new SyncFolders(data); var root = folders.Save(null, "Sync", rootPath, ["peer"]);
            var source = Path.Combine(data, "source.bin"); var bytes = new byte[SyncInbox.ChunkSize + 19]; new Random(8).NextBytes(bytes); File.WriteAllBytes(source, bytes);
            var request = new SyncUploadRequest(root.Id, "copy.bin", null, SyncFolders.FileHash(source), bytes.Length);
            var inbox = new SyncInbox(folders, data); var ticket = inbox.Begin("peer", request);
            inbox.Append("peer", ticket.Id, 0, bytes[..SyncInbox.ChunkSize]);
            var restarted = new SyncInbox(new SyncFolders(data), data); var resumed = restarted.Begin("peer", request);
            Assert.AreEqual((long)SyncInbox.ChunkSize, resumed.Offset);
            var tail = bytes[SyncInbox.ChunkSize..]; tail[0] ^= 1; restarted.Append("peer", resumed.Id, resumed.Offset, tail);
            Assert.ThrowsExactly<IOException>(() => restarted.Complete("peer", resumed.Id));
            Assert.IsFalse(File.Exists(Path.Combine(rootPath, "copy.bin")));
            Assert.AreEqual(0L, restarted.Begin("peer", request).Offset);
            restarted.Append("peer", ticket.Id, 0, bytes[..SyncInbox.ChunkSize]);
            restarted.Append("peer", ticket.Id, SyncInbox.ChunkSize, bytes[SyncInbox.ChunkSize..]);
            restarted.Complete("peer", ticket.Id);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(rootPath, "copy.bin")));
        }
        finally { Directory.Delete(data, true); }
    }
}
