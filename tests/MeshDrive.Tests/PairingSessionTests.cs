using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class PairingSessionTests
{
    [TestMethod]
    public void CompletesOnlyAfterBothAcceptAndRejectsOtherwise()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var session = Create(now.AddMinutes(2));
        Assert.AreEqual(PairingStatus.Waiting, session.StatusAt(now));
        Assert.IsTrue(session.RecordLocal(true, now));
        Assert.AreEqual(PairingStatus.Waiting, session.StatusAt(now));
        Assert.IsTrue(session.RecordRemote(true, now));
        Assert.AreEqual(PairingStatus.Completed, session.StatusAt(now));
        Assert.IsFalse(session.RecordLocal(true, now));

        var rejected = Create(now.AddMinutes(2));
        Assert.IsTrue(rejected.RecordLocal(false, now));
        Assert.AreEqual(PairingStatus.Rejected, rejected.StatusAt(now));
        Assert.IsFalse(rejected.RecordRemote(true, now));
    }

    [TestMethod]
    public void ExpiresWhenNotCompletedInTime()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var session = Create(now.AddSeconds(30));
        Assert.IsTrue(session.RecordLocal(true, now));
        Assert.AreEqual(PairingStatus.Expired, session.StatusAt(now.AddSeconds(31)));
        Assert.IsFalse(session.RecordRemote(true, now.AddSeconds(31)));
    }

    private static PairingSession Create(DateTimeOffset expiresAt) =>
        new(
            "session1",
            PairingTranscript.Create(
                new PairingSide("aaa", new string('A', 64), "N1"),
                new PairingSide("bbb", new string('B', 64), "N2")),
            "bbb",
            "Laptop",
            new string('B', 64),
            expiresAt);
}
