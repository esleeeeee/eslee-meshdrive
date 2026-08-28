using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class AgentIpcTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task StatusRoundTripSurvivesDisconnectAndReconnectUntilShutdown()
    {
        var pipeName = UniquePipe();
        var startedAt = new DateTimeOffset(2026, 8, 28, 9, 30, 0, TimeSpan.FromHours(9));
        await using var server = new AgentIpcServer(pipeName, startedAt, processId: 4242, version: "0.0.1-test");
        var run = server.RunAsync(CancellationToken.None);

        string firstSession;
        await using (var client = await AgentIpcClient.ConnectAsync(pipeName, TestTimeout, CancellationToken.None))
        {
            var hello = client.LastStatus;
            Assert.AreEqual(IpcProtocol.StateRunning, hello.State);
            Assert.AreEqual(4242, hello.ProcessId);
            Assert.AreEqual("0.0.1-test", hello.Version);
            Assert.AreEqual(startedAt, hello.StartedAt);
            firstSession = hello.SessionId;
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstSession));

            var status = await client.GetStatusAsync(CancellationToken.None);
            Assert.AreEqual(4242, status.ProcessId);
            Assert.AreEqual(firstSession, status.SessionId);
            Assert.IsGreaterThanOrEqualTo(1, status.ClientCount);
        }

        Assert.IsFalse(run.IsCompleted);
        Assert.IsFalse(server.IsShutdownRequested);

        await using (var client = await AgentIpcClient.ConnectAsync(pipeName, TestTimeout, CancellationToken.None))
        {
            var status = await client.GetStatusAsync(CancellationToken.None);
            Assert.AreEqual(4242, status.ProcessId);
            Assert.AreEqual(startedAt, status.StartedAt);
            Assert.AreNotEqual(firstSession, status.SessionId);
            await client.ShutdownAsync(CancellationToken.None);
        }

        await run.WaitAsync(TestTimeout);
        Assert.IsTrue(server.IsShutdownRequested);
    }

    [TestMethod]
    public async Task UnknownMessageReturnsErrorAndKeepsConnection()
    {
        var pipeName = UniquePipe();
        await using var server = new AgentIpcServer(pipeName, DateTimeOffset.Now);
        var run = server.RunAsync(CancellationToken.None);
        await using var client = await AgentIpcClient.ConnectAsync(pipeName, TestTimeout, CancellationToken.None);
        await using var raw = new RawIpcConnection(pipeName);
        await raw.ConnectAndHelloAsync(CancellationToken.None);
        var response = await raw.SendAsync(
            new IpcMessage { Type = "not-a-command", ProtocolVersion = 1, Id = 9 },
            CancellationToken.None);
        Assert.AreEqual(IpcProtocol.TypeError, response.Type);
        StringAssert.Contains(response.Error, "알 수 없는 메시지");

        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(IpcProtocol.StateRunning, status.State);

        await client.ShutdownAsync(CancellationToken.None);
        await run.WaitAsync(TestTimeout);
    }

    private static string UniquePipe() => "mdt-" + Guid.NewGuid().ToString("N");
}
