using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class StorageTests
{
    [TestMethod]
    public void SharesPersistAndPermissionsOverrideWithoutExposingLocalPaths()
    {
        using var fixture = new StorageFixture();
        var shared = fixture.Store.Save(null, "Music", fixture.Root, SharePermissions.ReadOnly,
            new() { ["blocked"] = SharePermissions.None, ["stream"] = SharePermissions.Browse | SharePermissions.Stream });
        File.WriteAllText(Path.Combine(fixture.Root, "song.mp3"), "original");
        Assert.AreEqual(shared, fixture.Store.Get(shared.Id) with { DeviceOverrides = shared.DeviceOverrides });
        Assert.HasCount(1, new SharedFolderStore(fixture.Data).Snapshot());
        Assert.IsEmpty(fixture.Service.ListShares("blocked"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => fixture.Service.Resolve("stream", shared.Id, "song.mp3", SharePermissions.Download));
        var entries = fixture.Service.ListEntries("reader", shared.Id, "");
        Assert.AreEqual("song.mp3", entries.Single().RelativePath);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(fixture.Service.ListShares("reader")).Contains(fixture.Root, StringComparison.Ordinal));
        fixture.Service.Paused = true;
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => fixture.Service.ListEntries("reader", shared.Id, ""));
    }

    [TestMethod]
    public void RejectsTraversalAbsoluteAlternateStreamsAndHiddenFiles()
    {
        using var fixture = new StorageFixture();
        foreach (var path in new[] { "../outside", "sub/../../outside", "C:/Windows", "a\\b", "song.mp3:secret", "a./file" })
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => SafeSharePath.Resolve(fixture.Root, path));
        var hidden = Path.Combine(fixture.Root, "hidden.txt");
        File.WriteAllText(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => SafeSharePath.Resolve(fixture.Root, "hidden.txt"));
        var share = fixture.Store.Save(null, "Files", fixture.Root, SharePermissions.ReadOnly);
        Assert.IsEmpty(fixture.Service.ListEntries("reader", share.Id, ""));
    }

    internal sealed class StorageFixture : IDisposable
    {
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "meshdrive-storage-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Data, "shared");
        public SharedFolderStore Store { get; }
        public StorageService Service { get; }
        public StorageFixture() { Directory.CreateDirectory(Root); Store = new(Data); Service = new(Store); }
        public void Dispose() => Directory.Delete(Data, true);
    }
}
