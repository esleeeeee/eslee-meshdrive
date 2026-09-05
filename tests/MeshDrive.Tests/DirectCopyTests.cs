using System.Net;
using System.Net.Http.Json;
using MeshDrive.Agent;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class DirectCopyTests
{
    [TestMethod]
    public async Task ThirdDeviceIssuesOnlyCommandsAndBothPermissionsAreRequired()
    {
        await using var control = await StorageHttpsTests.Node.CreateAsync("Control");
        await using var source = await StorageHttpsTests.Node.CreateAsync("Source");
        await using var target = await StorageHttpsTests.Node.CreateAsync("Target");
        await control.PairAsync(source); await control.PairAsync(target); await source.PairAsync(target);
        var bytes = new byte[QuickSendAdapter.ChunkSize + 127]; new Random(87).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(source.Root, "direct.bin"), bytes);
        var from = source.Storage.Shares.Save(null, "Source", source.Root, SharePermissions.ReadOnly);
        var to = target.Storage.Shares.Save(null, "Target", target.Root, SharePermissions.All);
        var coordinator = new StorageCoordinator(control.Storage, control.Remote);
        var command = new StorageCommand { Action = "copy-direct", DeviceId = source.Identity.DeviceId, ShareId = from.Id, Path = "direct.bin",
            TargetDeviceId = target.Identity.DeviceId, TargetShareId = to.Id, Destination = "" };
        var started = await coordinator.HandleAsync(new() { Type = "storage", Storage = command }, CancellationToken.None);
        Assert.IsNotNull(started?.StorageResult?.Value, started?.Error);
        await target.Transfers.WaitAsync(started.StorageResult.Value);
        Assert.AreEqual("완료", target.Transfers.RemoteProgress(control.Identity.DeviceId, started.StorageResult.Value).State);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(target.Root, "direct.bin")));
        Assert.IsFalse(Directory.Exists(Path.Combine(control.Data, "transfer-parts")));
        Assert.IsEmpty(Directory.GetFiles(control.Root));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Task.FromResult(target.Transfers.RemoteProgress(source.Identity.DeviceId, started.StorageResult.Value)));

        target.Storage.Shares.Save(to.Id, to.Name, to.LocalPath, SharePermissions.All, new() { [control.Identity.DeviceId] = SharePermissions.ReadOnly });
        var deniedTarget = await coordinator.HandleAsync(new() { Type = "storage", Storage = command }, CancellationToken.None);
        Assert.AreEqual(IpcProtocol.TypeError, deniedTarget?.Type);
        source.Storage.Shares.Save(from.Id, from.Name, from.LocalPath, SharePermissions.ReadOnly, new() { [control.Identity.DeviceId] = SharePermissions.Browse });
        var deniedSource = await coordinator.HandleAsync(new() { Type = "storage", Storage = command }, CancellationToken.None);
        Assert.AreEqual(IpcProtocol.TypeError, deniedSource?.Type);
    }

    [TestMethod]
    public async Task GrantIsBoundToTargetPathAndRequestersTrust()
    {
        await using var control = await StorageHttpsTests.Node.CreateAsync("Control");
        await using var source = await StorageHttpsTests.Node.CreateAsync("Source");
        await using var target = await StorageHttpsTests.Node.CreateAsync("Target");
        await control.PairAsync(source); await control.PairAsync(target); await source.PairAsync(target);
        await File.WriteAllTextAsync(Path.Combine(source.Root, "one.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(source.Root, "two.txt"), "second");
        var share = source.Storage.Shares.Save(null, "Source", source.Root, SharePermissions.ReadOnly);
        using var response = await control.Remote.SendAsync(source.Identity.DeviceId, HttpMethod.Post, "/v1/secure/storage/copy-authorize",
            r => r.Content = JsonContent.Create(new CopyGrantRequest(target.Identity.DeviceId, share.Id, "one.txt")), CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var ticket = await response.Content.ReadFromJsonAsync<CopyTicket>(); Assert.IsNotNull(ticket);
        using var wrongTarget = await control.Remote.SendAsync(source.Identity.DeviceId, HttpMethod.Get, "/v1/secure/storage/copy-grant?token=" + ticket.Token, null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongTarget.StatusCode);
        using var wrongPath = await target.Remote.SendAsync(source.Identity.DeviceId, HttpMethod.Get,
            RemoteStorageClient.Resource("manifest", share.Id, "two.txt") + "&copyToken=" + ticket.Token, null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongPath.StatusCode);
        source.Trust.Unpair(control.Identity.DeviceId);
        using var revoked = await target.Remote.SendAsync(source.Identity.DeviceId, HttpMethod.Get, "/v1/secure/storage/copy-grant?token=" + ticket.Token, null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, revoked.StatusCode);
    }
}
