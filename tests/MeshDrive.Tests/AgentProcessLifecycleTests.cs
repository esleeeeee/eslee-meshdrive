using System.Diagnostics;
using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class AgentProcessLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task AgentProcessSurvivesClientDisconnectAndStopsOnlyOnShutdown()
    {
        var pipeName = "mdt-proc-" + Guid.NewGuid().ToString("N");
        var mutexName = @"Local\mdt-proc-" + Guid.NewGuid().ToString("N");
        var agentPath = AgentProcessLauncher.ResolveExecutablePath();
        Assert.IsTrue(File.Exists(agentPath), $"Agent 실행 파일이 없습니다: {agentPath}");

        Process? agent = null;
        AgentIpcClient? client = null;
        try
        {
            client = await AgentIpcClient.ConnectOrStartAsync(
                pipeName,
                agentPath,
                ["--pipe-name", pipeName, "--mutex-name", mutexName, "--disable-mdns"],
                TestTimeout,
                CancellationToken.None);
            var first = await client.GetStatusAsync(CancellationToken.None);
            Assert.AreEqual(IpcProtocol.StateRunning, first.State);
            Assert.IsGreaterThan(0, first.ProcessId);
            agent = Process.GetProcessById(first.ProcessId);
            var originalPid = first.ProcessId;
            var originalStart = first.StartedAt;
            var firstSession = first.SessionId;

            await client.DisposeAsync();
            client = null;
            await Task.Delay(300);
            Assert.IsFalse(agent.HasExited, "GUI 연결을 끊은 뒤에도 Agent가 살아 있어야 합니다.");

            client = await AgentIpcClient.ConnectAsync(pipeName, TestTimeout, CancellationToken.None);
            var second = await client.GetStatusAsync(CancellationToken.None);
            Assert.AreEqual(originalPid, second.ProcessId);
            Assert.AreEqual(originalStart, second.StartedAt);
            Assert.AreNotEqual(firstSession, second.SessionId);

            await client.ShutdownAsync(CancellationToken.None);
            await client.DisposeAsync();
            client = null;

            Assert.IsTrue(agent.WaitForExit(10000), "전체 종료 명령 뒤에 Agent가 종료되어야 합니다.");
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            if (agent is { HasExited: false })
            {
                agent.Kill(entireProcessTree: true);
                agent.WaitForExit(5000);
            }

            agent?.Dispose();
        }
    }

    [TestMethod]
    public async Task SecondAgentStartReusesExistingProcess()
    {
        var pipeName = "mdt-once-" + Guid.NewGuid().ToString("N");
        var mutexName = @"Local\mdt-once-" + Guid.NewGuid().ToString("N");
        var agentPath = AgentProcessLauncher.ResolveExecutablePath();
        var arguments = new[] { "--pipe-name", pipeName, "--mutex-name", mutexName, "--disable-mdns" };

        Process? first = null;
        Process? second = null;
        AgentIpcClient? client = null;
        try
        {
            first = AgentProcessLauncher.Start(agentPath, arguments);
            client = await AgentIpcClient.ConnectAsync(pipeName, TestTimeout, CancellationToken.None);
            var status = await client.GetStatusAsync(CancellationToken.None);

            second = AgentProcessLauncher.Start(agentPath, arguments);
            Assert.IsTrue(second.WaitForExit(5000), "이미 Agent가 있으면 두 번째 프로세스는 바로 종료되어야 합니다.");
            Assert.AreEqual(0, second.ExitCode);

            var again = await client.GetStatusAsync(CancellationToken.None);
            Assert.AreEqual(status.ProcessId, again.ProcessId);
            await client.ShutdownAsync(CancellationToken.None);
            Assert.IsTrue(first.WaitForExit(10000));
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            KillIfRunning(first);
            KillIfRunning(second);
        }
    }

    private static void KillIfRunning(Process? process)
    {
        if (process is null)
        {
            return;
        }

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
}
