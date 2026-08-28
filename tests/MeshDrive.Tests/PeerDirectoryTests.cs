using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class PeerDirectoryTests
{
    [TestMethod]
    public void IgnoresSelfAndTracksOnlineThenOffline()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var directory = new PeerDirectory("localid", TimeSpan.FromSeconds(30));
        directory.Upsert(new PeerSighting("localid", "ThisPC", "192.168.0.2", 41241), now);
        directory.Upsert(new PeerSighting("laptop1", "Laptop", "192.168.0.12", 41241), now);
        directory.Upsert(new PeerSighting("desktop1", "Desktop", "192.168.0.5", 41241), now);

        var snapshot = directory.Snapshot();
        Assert.HasCount(2, snapshot);
        Assert.AreEqual("Desktop", snapshot[0].Name);
        Assert.AreEqual("Laptop", snapshot[1].Name);
        Assert.IsTrue(snapshot.All(static peer => peer.IsOnline));
        Assert.IsFalse(snapshot.Any(static peer => peer.DeviceId == "localid"));

        Assert.AreEqual(0, directory.Expire(now.AddSeconds(10)));
        Assert.AreEqual(2, directory.Expire(now.AddSeconds(31)));
        snapshot = directory.Snapshot();
        Assert.HasCount(2, snapshot);
        Assert.IsTrue(snapshot.All(static peer => !peer.IsOnline));
        Assert.AreEqual("192.168.0.12", snapshot.Single(static peer => peer.DeviceId == "laptop1").Ipv4);
    }

    [TestMethod]
    public void MarkOfflineKeepsLastAddressAndAllowsRediscovery()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var directory = new PeerDirectory("localid", TimeSpan.FromSeconds(30));
        directory.Upsert(new PeerSighting("laptop1", "Laptop", "192.168.0.12", 41241), now);
        directory.MarkOffline("laptop1", now.AddSeconds(5));
        var offline = directory.Snapshot().Single();
        Assert.IsFalse(offline.IsOnline);
        Assert.AreEqual("192.168.0.12", offline.Ipv4);

        directory.Upsert(new PeerSighting("laptop1", "Laptop-2", "192.168.0.20", 41241), now.AddSeconds(6));
        var online = directory.Snapshot().Single();
        Assert.IsTrue(online.IsOnline);
        Assert.AreEqual("Laptop-2", online.Name);
        Assert.AreEqual("192.168.0.20", online.Ipv4);
    }
}
