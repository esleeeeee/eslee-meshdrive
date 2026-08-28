using System.Net;
using System.Net.Sockets;
using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class PairingHttpsTests
{
    [TestMethod]
    public async Task TwoHostsPairWithMatchingSasThenTrustHttpsAndRejectUnpaired()
    {
        await using var nodeA = await TestHttpsNode.StartAsync("PC-A");
        await using var nodeB = await TestHttpsNode.StartAsync("PC-B");
        await using var nodeC = await TestHttpsNode.StartAsync("PC-C");

        var started = await nodeA.Coordinator.StartOutgoingAsync(
            nodeB.Identity.DeviceId,
            IPAddress.Loopback.ToString(),
            nodeB.Port,
            CancellationToken.None);
        Assert.AreEqual("waiting", started.Status);

        PairingSnapshot? incoming = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            incoming = nodeB.Coordinator.CurrentPairing();
            if (incoming is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.IsNotNull(incoming);
        Assert.AreEqual(started.Sas, incoming.Sas);
        Assert.AreEqual(started.SessionId, incoming.SessionId);

        await nodeA.Coordinator.DecideLocalAsync(true, CancellationToken.None);
        var completed = await nodeB.Coordinator.DecideLocalAsync(true, CancellationToken.None);
        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("completed", nodeA.Coordinator.CurrentPairing()?.Status);

        await nodeA.Coordinator.PingTrustedAsync(nodeB.Identity.DeviceId, CancellationToken.None);
        await nodeB.Coordinator.PingTrustedAsync(nodeA.Identity.DeviceId, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => nodeC.Coordinator.PingTrustedAsync(nodeB.Identity.DeviceId, CancellationToken.None));

        var clientC = new PeerHttpsClient(nodeC.Credential);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => clientC.PingAsync(
                IPAddress.Loopback.ToString(),
                nodeB.Port,
                nodeB.Credential.Fingerprint,
                CancellationToken.None));
        StringAssert.Contains(error.Message, "403");

        Assert.IsTrue(nodeB.Coordinator.ListTrusted().Any(peer => peer.DeviceId == nodeA.Identity.DeviceId));
        nodeB.Trust.Unpair(nodeA.Identity.DeviceId);
        var rejectedAfterUnpair = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => nodeA.Coordinator.PingTrustedAsync(nodeB.Identity.DeviceId, CancellationToken.None));
        StringAssert.Contains(rejectedAfterUnpair.Message, "403");

        nodeA.Trust.Unpair(nodeB.Identity.DeviceId);
        var localDenied = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => nodeA.Coordinator.PingTrustedAsync(nodeB.Identity.DeviceId, CancellationToken.None));
        StringAssert.Contains(localDenied.Message, "신뢰되지 않은 기기");
    }

    private sealed class TestHttpsNode : IAsyncDisposable
    {
        private TestHttpsNode(
            string directory,
            DeviceIdentity identity,
            DeviceCredential credential,
            TrustedPeerStore trust,
            PairingCoordinator coordinator,
            AgentHttpsHost host)
        {
            Directory = directory;
            Identity = identity;
            Credential = credential;
            Trust = trust;
            Coordinator = coordinator;
            Host = host;
        }

        public string Directory { get; }

        public DeviceIdentity Identity { get; }

        public DeviceCredential Credential { get; }

        public TrustedPeerStore Trust { get; }

        public PairingCoordinator Coordinator { get; }

        public AgentHttpsHost Host { get; }

        public int Port => Host.Port;

        public static async Task<TestHttpsNode> StartAsync(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "meshdrive-https-" + Guid.NewGuid().ToString("N"));
            var identity = DeviceIdentityStore.LoadOrCreate(directory, name);
            var credential = DeviceCredentialStore.LoadOrCreate(directory, identity.DeviceId);
            var trust = new TrustedPeerStore(directory);
            var peers = new PeerDirectory(identity.DeviceId, DiscoveryNames.OfflineAfter);
            var port = GetFreePort();
            var coordinator = new PairingCoordinator(identity, credential, trust, peers, port);
            var host = new AgentHttpsHost(identity, credential, coordinator, port);
            Assert.IsTrue(await host.TryStartAsync(CancellationToken.None));
            return new TestHttpsNode(directory, identity, credential, trust, coordinator, host);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
