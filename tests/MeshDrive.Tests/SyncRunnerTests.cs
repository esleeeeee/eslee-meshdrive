using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class SyncRunnerTests
{
    [TestMethod]
    public async Task TwoWaySyncPreservesOfflineConflictsAndRestoresDeletedVersions()
    {
        await using var a = await StorageHttpsTests.Node.CreateAsync("A");
        await using var b = await StorageHttpsTests.Node.CreateAsync("B"); await a.PairAsync(b);
        var left = a.Sync.Save(null, "Left", a.Root, [b.Identity.DeviceId]);
        var right = b.Sync.Save(null, "Right", b.Root, [a.Identity.DeviceId]);
        var job = new SyncJob(Guid.NewGuid().ToString("N"), left.Id, b.Identity.DeviceId, right.Id, SyncMode.TwoWay, true);
        await using (var first = new SyncRunner(a.Sync, new(a.Remote, a.Data), a.Data) { IsTrusted = id => a.Trust.Snapshot().Any(p => p.DeviceId == id) })
        {
            first.Save(job);
            await File.WriteAllTextAsync(Path.Combine(a.Root, "note.txt"), "initial");
            await first.RunAsync(job.Id, CancellationToken.None);
            Assert.AreEqual("동기화 완료", first.Status.Single().State, first.Status.Single().Error);
            Assert.AreEqual("initial", await File.ReadAllTextAsync(Path.Combine(b.Root, "note.txt")));
        }
        await File.WriteAllTextAsync(Path.Combine(a.Root, "note.txt"), "left offline edit");
        await File.WriteAllTextAsync(Path.Combine(b.Root, "note.txt"), "right offline edit");
        await using var restarted = new SyncRunner(a.Sync, new(a.Remote, a.Data), a.Data) { IsTrusted = _ => true };
        await restarted.RunAsync(job.Id, CancellationToken.None);
        Assert.AreEqual(1, restarted.Status.Single().Conflicts, restarted.Status.Single().Error);
        Assert.AreEqual("left offline edit", await File.ReadAllTextAsync(Path.Combine(a.Root, "note.txt")));
        Assert.AreEqual("right offline edit", await File.ReadAllTextAsync(Path.Combine(b.Root, "note.txt")));
        Assert.HasCount(2, Directory.GetFiles(a.Root, "*.conflict-*.txt"));
        Assert.HasCount(2, Directory.GetFiles(b.Root, "*.conflict-*.txt"));
        await restarted.RunAsync(job.Id, CancellationToken.None);
        Assert.AreEqual(0, restarted.Status.Single().Conflicts);
        // A subsequent intentional local edit resolves the divergence and is propagated.
        await File.WriteAllTextAsync(Path.Combine(a.Root, "note.txt"), "resolved");
        await restarted.RunAsync(job.Id, CancellationToken.None);
        Assert.AreEqual("resolved", await File.ReadAllTextAsync(Path.Combine(b.Root, "note.txt")));
        File.Delete(Path.Combine(a.Root, "note.txt"));
        await restarted.RunAsync(job.Id, CancellationToken.None);
        Assert.IsFalse(File.Exists(Path.Combine(b.Root, "note.txt")));
        var version = b.Sync.Versions(right.Id).First(v => v.Path == "note.txt");
        b.Sync.Restore(right.Id, version.Id);
        Assert.AreEqual("resolved", await File.ReadAllTextAsync(Path.Combine(b.Root, "note.txt")));
    }
}
