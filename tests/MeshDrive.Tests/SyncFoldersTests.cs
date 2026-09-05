using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class SyncFoldersTests
{
    [TestMethod]
    public void ExplicitRootsPreserveReplacedAndDeletedVersionsAndRestore()
    {
        using var files = new Fixture();
        var folders = new SyncFolders(files.Data);
        var root = folders.Save(null, "Sync", files.Root, ["allowed"]);
        var original = Path.Combine(files.Root, "note.txt"); File.WriteAllText(original, "old");
        var oldHash = SyncFolders.FileHash(original);
        var incoming = Path.Combine(files.Data, "incoming"); File.WriteAllText(incoming, "new");
        var newHash = SyncFolders.FileHash(incoming);
        folders.Apply(root.Id, "note.txt", oldHash, incoming, newHash, "allowed");
        Assert.AreEqual("new", File.ReadAllText(original));
        Assert.AreEqual(oldHash, folders.Versions(root.Id).Single().Hash);
        folders.Apply(root.Id, "note.txt", newHash, null, null, "allowed");
        Assert.IsFalse(File.Exists(original)); Assert.HasCount(2, folders.Versions(root.Id));
        var restarted = new SyncFolders(files.Data);
        Assert.AreEqual(root.Id, restarted.Snapshot().Single().Id);
        restarted.Restore(root.Id, restarted.Versions(root.Id).Single(v => v.Hash == oldHash).Id);
        Assert.AreEqual("old", File.ReadAllText(original));
        Assert.AreEqual(oldHash, restarted.Inventory(root.Id, "allowed").Single().Hash);
    }

    [TestMethod]
    public void ChangedFilesBadBytesAndUnapprovedRootsCannotBeMutated()
    {
        using var files = new Fixture();
        var folders = new SyncFolders(files.Data);
        var root = folders.Save(null, "Sync", files.Root, ["allowed"]);
        var original = Path.Combine(files.Root, "note.txt"); File.WriteAllText(original, "independent edit");
        var incoming = Path.Combine(files.Data, "incoming"); File.WriteAllText(incoming, "new");
        var oldHash = SyncFolders.FileHash(original); var newHash = SyncFolders.FileHash(incoming);
        Assert.ThrowsExactly<IOException>(() => folders.Apply(root.Id, "note.txt", "stale", incoming, newHash, "allowed"));
        Assert.ThrowsExactly<IOException>(() => folders.Apply(root.Id, "note.txt", oldHash, incoming, "wrong", "allowed"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => folders.Apply(root.Id, "note.txt", oldHash, incoming, newHash, "intruder"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => folders.Apply("ordinary-share-id", "note.txt", oldHash, incoming, newHash, "allowed"));
        foreach (var path in new[] { "../escape", "C:/escape", "sub/../../escape", ".private/key", "" })
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => folders.Apply(root.Id, path, null, incoming, newHash, "allowed"));
        Assert.AreEqual("independent edit", File.ReadAllText(original)); Assert.IsEmpty(folders.Versions(root.Id));
        folders.Apply(root.Id, "nested/folder/new.txt", null, incoming, newHash, "allowed");
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(files.Root, "nested", "folder", "new.txt")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "meshdrive-sync-test-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(_path, "data");
        public string Root => Path.Combine(_path, "root");
        public Fixture() { Directory.CreateDirectory(Data); Directory.CreateDirectory(Root); }
        public void Dispose() => Directory.Delete(_path, true);
    }
}
