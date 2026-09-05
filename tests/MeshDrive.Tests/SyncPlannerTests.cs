using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class SyncPlannerTests
{
    [TestMethod]
    public void TwoWayDistinguishesCreationDeletionAndConcurrentChanges()
    {
        Assert.AreEqual(SyncAction.CopyLeftToRight, SyncPlanner.Decide("new", null, null, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.CopyRightToLeft, SyncPlanner.Decide(null, "new", null, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.Conflict, SyncPlanner.Decide("a", "b", null, SyncMode.TwoWay));
        var baseline = new SyncBaseline("old", "old");
        Assert.AreEqual(SyncAction.DeleteRight, SyncPlanner.Decide(null, "old", baseline, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.DeleteLeft, SyncPlanner.Decide("old", null, baseline, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.Conflict, SyncPlanner.Decide(null, "modified", baseline, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.Conflict, SyncPlanner.Decide("modified", null, baseline, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.Conflict, SyncPlanner.Decide("a", "b", baseline, SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.None, SyncPlanner.Decide("a", "b", new("a", "b"), SyncMode.TwoWay));
        Assert.AreEqual(SyncAction.None, SyncPlanner.Decide("same", "same", baseline, SyncMode.TwoWay));
    }
    [TestMethod]
    public void OneWayPreservesIndependentDestinationEdits()
    {
        var baseline = new SyncBaseline("old", "old");
        Assert.AreEqual(SyncAction.Conflict, SyncPlanner.Decide("new", "other", baseline, SyncMode.Push));
        Assert.AreEqual(SyncAction.Conflict, SyncPlanner.Decide("other", "new", baseline, SyncMode.Pull));
        Assert.AreEqual(SyncAction.CopyLeftToRight, SyncPlanner.Decide("new", "old", baseline, SyncMode.Push));
        Assert.AreEqual(SyncAction.CopyRightToLeft, SyncPlanner.Decide("old", "new", baseline, SyncMode.Pull));
        Assert.AreEqual("folder/photo.conflict-aaaaaaaaaaaa.jpg", SyncPlanner.ConflictPath("folder/photo.jpg", new string('A', 64)));
    }
}
