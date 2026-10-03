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
    public void SyncInboxExpiresAbandonedSessionsAndPreservesActiveResumeAndAppliedCleanup()
    {
        var data = Path.Combine(Path.GetTempPath(), "meshdrive-inbox-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var rootPath = Directory.CreateDirectory(Path.Combine(data, "root")).FullName;
            var folders = new SyncFolders(data); var root = folders.Save(null, "Sync", rootPath, ["peer"]);
            var source = Path.Combine(data, "source"); File.WriteAllText(source, "bytes");
            var request = new SyncUploadRequest(root.Id, "active", null, SyncFolders.FileHash(source), 5);
            var inbox = new SyncInbox(folders, data);
            var active = inbox.Begin("peer", request); inbox.Append("peer", active.Id, 0, File.ReadAllBytes(source));
            var stale = inbox.Begin("peer", request with { Path = "stale" });
            var staging = Path.Combine(data, "sync-inbox");
            File.SetLastWriteTimeUtc(Path.Combine(staging, stale.Id + ".json"), DateTime.UtcNow.AddDays(-8));
            Assert.AreEqual(5L, inbox.Begin("peer", request).Offset);
            Assert.IsFalse(File.Exists(Path.Combine(staging, stale.Id + ".json")));
            Assert.IsFalse(File.Exists(Path.Combine(staging, stale.Id + ".part")));
            // Simulate process loss after Apply but before staging cleanup.
            folders.Apply(root.Id, request.Path, null, source, request.NewHash, "peer");
            new SyncInbox(folders, data).Complete("peer", active.Id);
            Assert.IsEmpty(Directory.GetFiles(staging));
            Assert.ThrowsExactly<IOException>(() => new SyncInbox(folders, data, _ => 4).Begin("peer", request with { Path = "out-of-space" }));
        }
        finally { Directory.Delete(data, true); }
    }

    [TestMethod]
    public void SyncInboxSupportsLargeDeclarationsAndIsolatesCorruptMetadata()
    {
        var data = Path.Combine(Path.GetTempPath(), "meshdrive-inbox-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            const long gib = 1024L * 1024 * 1024;
            var folders = new SyncFolders(data);
            var root = folders.Save(null, "Sync", Directory.CreateDirectory(Path.Combine(data, "root")).FullName, ["peer"]);
            long free = 160 * gib;
            var inbox = new SyncInbox(folders, data, _ => free);
            var request = new SyncUploadRequest(root.Id, "large-100", null, new string('A', 64), 100 * gib);
            var large = inbox.Begin("peer", request);
            var second = inbox.Begin("peer", request with { Path = "large-50", Size = 50 * gib });
            var staging = Path.Combine(data, "sync-inbox");
            Assert.AreEqual(0L, new FileInfo(Path.Combine(staging, large.Id + ".part")).Length);
            Assert.ThrowsExactly<IOException>(() => inbox.Begin("peer", request with { Path = "over-reserved", Size = 11 * gib }));
            free = 0;
            Assert.AreEqual(large.Id, inbox.Begin("peer", request).Id); // no eviction when free space drops
            var damaged = Path.Combine(staging, second.Id + ".json");
            File.WriteAllText(damaged, "{broken"); File.SetLastWriteTimeUtc(damaged, DateTime.UtcNow.AddDays(-8)); File.WriteAllText(Path.Combine(staging, second.Id + ".part"), "recoverable bytes");
            File.WriteAllText(Path.Combine(staging, "unrelated.json"), "not ours");
            free = 160 * gib;
            var healthy = inbox.Begin("peer", request with { Path = "replacement-50", Size = 50 * gib });
            Assert.IsNotNull(healthy);
            Assert.IsTrue(File.Exists(Path.Combine(staging, large.Id + ".json")));
            Assert.AreEqual("not ours", File.ReadAllText(Path.Combine(staging, "unrelated.json")));
            var quarantine = Directory.GetDirectories(Path.Combine(staging, "quarantine")).Single();
            Assert.AreEqual("{broken", File.ReadAllText(Path.Combine(quarantine, second.Id + ".json")));
            Assert.AreEqual("recoverable bytes", File.ReadAllText(Path.Combine(quarantine, second.Id + ".part")));
            Assert.AreEqual(large.Id, new SyncInbox(folders, data, _ => 0).Begin("peer", request).Id);
        }
        finally { Directory.Delete(data, true); }
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
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(data, "sync-inbox")));
            Assert.IsTrue(restarted.Begin("peer", request).Completed);
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(data, "sync-inbox")));
        }
        finally { Directory.Delete(data, true); }
    }
}
