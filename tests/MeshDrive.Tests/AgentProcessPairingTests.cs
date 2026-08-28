using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MeshDrive.Agent;
using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class AgentProcessPairingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task TwoAgentProcessesPairApproveAndServeTrustedHttps()
    {
        var agentPath = AgentProcessLauncher.ResolveExecutablePath();
        Assert.IsTrue(File.Exists(agentPath), $"Agent 실행 파일이 없습니다: {agentPath}");

        await using var a = await StartedAgent.StartAsync(agentPath);
        await using var b = await StartedAgent.StartAsync(agentPath);

        var started = await a.Client.StartPairingAsync(
            b.DeviceId,
            IPAddress.Loopback.ToString(),
            b.HttpsPort,
            CancellationToken.None);
        Assert.IsFalse(string.IsNullOrWhiteSpace(started.Sas));

        IpcMessage? incoming = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            incoming = await b.Client.GetPairingAsync(CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(incoming.Sas))
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.IsNotNull(incoming);
        Assert.AreEqual(started.Sas, incoming.Sas);

        await a.Client.DecidePairingAsync(true, CancellationToken.None);
        var completed = await b.Client.DecidePairingAsync(true, CancellationToken.None);
        Assert.AreEqual("completed", completed.PairingStatus);

        var ping = await a.Client.SecurePingAsync(b.DeviceId, CancellationToken.None);
        Assert.IsTrue(ping.Succeeded);

        var trusted = await a.Client.GetTrustedAsync(CancellationToken.None);
        Assert.IsTrue((trusted.Trusted ?? []).Any(item => item.DeviceId == b.DeviceId));

        await a.Client.UnpairAsync(b.DeviceId, CancellationToken.None);
        var denied = await Assert.ThrowsExactlyAsync<IOException>(
            () => a.Client.SecurePingAsync(b.DeviceId, CancellationToken.None));
        StringAssert.Contains(denied.Message, "신뢰되지 않은 기기");
    }

    private sealed class StartedAgent : IAsyncDisposable
    {
        private StartedAgent(Process process, AgentIpcClient client, string deviceId, int httpsPort, string directory)
        {
            Process = process;
            Client = client;
            DeviceId = deviceId;
            HttpsPort = httpsPort;
            Directory = directory;
        }

        public Process Process { get; }

        public AgentIpcClient Client { get; }

        public string DeviceId { get; }

        public int HttpsPort { get; }

        public string Directory { get; }

        public static async Task<StartedAgent> StartAsync(string agentPath)
        {
            var directory = Path.Combine(Path.GetTempPath(), "meshdrive-proc-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var pipe = "mdt-pair-" + Guid.NewGuid().ToString("N");
            var mutex = @"Local\mdt-pair-" + Guid.NewGuid().ToString("N");
            var port = GetFreePort();
            var process = AgentProcessLauncher.Start(
                agentPath,
                [
                    "--pipe-name", pipe,
                    "--mutex-name", mutex,
                    "--data-dir", directory,
                    "--disable-mdns",
                    "--https-port", port.ToString(CultureInfo.InvariantCulture),
                ]);
            try
            {
                var client = await AgentIpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(15), CancellationToken.None);
                var status = await client.GetStatusAsync(CancellationToken.None);
                Assert.IsFalse(string.IsNullOrWhiteSpace(status.DeviceId));
                return new StartedAgent(process, client, status.DeviceId, port, directory);
            }
            catch
            {
                Kill(process);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    await Client.ShutdownAsync(CancellationToken.None);
                    Process.WaitForExit(8000);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                await Client.DisposeAsync();
                Kill(Process);
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
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
