using System.Net;
using System.Net.Sockets;
using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class StorageHttpsTests
{
    [TestMethod]
    public async Task LoopbackBridgeRelaysRangeAndExpiresTokens()
    {
        await using var a = await Node.CreateAsync("A"); await using var b = await Node.CreateAsync("B");
        await a.PairAsync(b);
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(b.Root, "movie.mp4"), bytes);
        var share = b.Storage.Shares.Save(null, "Media", b.Root, SharePermissions.ReadOnly);
        var clock = new TestClock();
        await using var bridge = new LocalStreamBridge(a.Remote, clock);
        await bridge.StartAsync(CancellationToken.None);
        var url = await bridge.CreateAsync(b.Identity.DeviceId, share.Id, "movie.mp4", CancellationToken.None);
        Assert.AreEqual("127.0.0.1", new Uri(url).Host);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Range = new(12, 89);
        using var partial = await http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
        CollectionAssert.AreEqual(bytes[12..90], await partial.Content.ReadAsByteArrayAsync());
        using var bad = await http.GetAsync(new Uri(bridge.BaseAddress!, "/stream/bad/movie.mp4"));
        Assert.AreEqual(HttpStatusCode.Gone, bad.StatusCode);
        clock.Now += LocalStreamBridge.IdleLifetime;
        using var expired = await http.GetAsync(url); Assert.AreEqual(HttpStatusCode.Gone, expired.StatusCode);
        var renewed = await bridge.CreateAsync(b.Identity.DeviceId, share.Id, "movie.mp4", CancellationToken.None);
        b.Trust.Unpair(a.Identity.DeviceId);
        using var unpaired = await http.GetAsync(renewed); Assert.AreEqual(HttpStatusCode.Forbidden, unpaired.StatusCode);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [TestMethod]
    public async Task AuthenticatedBrowseAndOriginalRangeBytesEnforcePermissions()
    {
        await using var a = await Node.CreateAsync("A");
        await using var b = await Node.CreateAsync("B");
        await a.PairAsync(b);
        var bytes = Enumerable.Range(0, 16384).Select(i => (byte)(i % 251)).ToArray();
        foreach (var name in new[] { "music.mp3", "movie.mp4" }) await File.WriteAllBytesAsync(Path.Combine(b.Root, name), bytes);
        var share = b.Storage.Shares.Save(null, "Media", b.Root, SharePermissions.ReadOnly);
        var shares = await a.Remote.GetAsync<List<RemoteShare>>(b.Identity.DeviceId, "/v1/secure/storage/shares", CancellationToken.None);
        Assert.AreEqual("Media", shares.Single().Name);
        var entries = await a.Remote.GetAsync<List<RemoteEntry>>(b.Identity.DeviceId, RemoteStorageClient.Resource("entries", share.Id, ""), CancellationToken.None);
        Assert.HasCount(2, entries);
        foreach (var name in new[] { "music.mp3", "movie.mp4" })
        {
            var path = RemoteStorageClient.Resource("content", share.Id, name);
            using var full = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get, path, null, CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.OK, full.StatusCode);
            CollectionAssert.AreEqual(bytes, await full.Content.ReadAsByteArrayAsync());
            using var head = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Head, path, null, CancellationToken.None);
            Assert.AreEqual((long)bytes.Length, head.Content.Headers.ContentLength);
            Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync());
            using var range = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get, path,
                r => r.Headers.Range = new(100, 299), CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.PartialContent, range.StatusCode);
            CollectionAssert.AreEqual(bytes[100..300], await range.Content.ReadAsByteArrayAsync());
            using var invalid = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get, path,
                r => r.Headers.Range = new(bytes.Length + 1, null), CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        }
        using var escape = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get,
            RemoteStorageClient.Resource("content", share.Id, "../outside"), null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, escape.StatusCode);
        b.Storage.Shares.Save(share.Id, share.Name, share.LocalPath, SharePermissions.Browse | SharePermissions.Stream);
        using var denied = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get,
            RemoteStorageClient.Resource("content", share.Id, "music.mp3") + "&purpose=download", null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        b.Trust.Unpair(a.Identity.DeviceId);
        using var unpaired = await a.Remote.SendAsync(b.Identity.DeviceId, HttpMethod.Get, "/v1/secure/storage/shares", null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Forbidden, unpaired.StatusCode);
    }

    internal sealed class Node : IAsyncDisposable
    {
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "meshdrive-node-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Data, "share");
        public DeviceIdentity Identity { get; private set; } = null!;
        public DeviceCredential Credential { get; private set; } = null!;
        public TrustedPeerStore Trust { get; private set; } = null!;
        public PairingCoordinator Pairing { get; private set; } = null!;
        public StorageService Storage { get; private set; } = null!;
        public RemoteStorageClient Remote { get; private set; } = null!;
        public AgentHttpsHost Host { get; private set; } = null!;
        public FileTransferService Transfers { get; private set; } = null!;
        public SyncFolders Sync { get; private set; } = null!;
        public static async Task<Node> CreateAsync(string name)
        {
            var node = new Node(); Directory.CreateDirectory(node.Root);
            node.Identity = DeviceIdentityStore.LoadOrCreate(node.Data, name);
            node.Credential = DeviceCredentialStore.LoadOrCreate(node.Data, node.Identity.DeviceId);
            node.Trust = new(node.Data);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            node.Pairing = new(node.Identity, node.Credential, node.Trust, new(node.Identity.DeviceId, DiscoveryNames.OfflineAfter), port);
            node.Storage = new(new(node.Data));
            node.Remote = new(node.Credential, node.Pairing);
            node.Transfers = new(node.Remote, node.Storage, node.Data);
            node.Sync = new(node.Data);
            node.Host = new(node.Identity, node.Credential, node.Pairing, port) { Storage = node.Storage, Thumbnails = new PhotoCache(Path.Combine(node.Data, "thumbnails")), Transfers = node.Transfers, Sync = node.Sync, SyncInbox = new(node.Sync, node.Data) };
            Assert.IsTrue(await node.Host.TryStartAsync(CancellationToken.None));
            return node;
        }
        public async Task PairAsync(Node other)
        {
            await Pairing.StartOutgoingAsync(other.Identity.DeviceId, "127.0.0.1", other.Host.Port, CancellationToken.None);
            await Pairing.DecideLocalAsync(true, CancellationToken.None);
            await other.Pairing.DecideLocalAsync(true, CancellationToken.None);
        }
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Transfers.DisposeAsync(); Credential.Certificate.Dispose(); Directory.Delete(Data, true); }
    }
}
