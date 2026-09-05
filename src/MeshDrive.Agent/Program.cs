namespace MeshDrive.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--shutdown")
        {
            try { await using var client = await MeshDrive.Protocol.AgentIpcClient.ConnectAsync(MeshDrive.Core.IpcNames.DefaultPipeName, TimeSpan.FromSeconds(2), CancellationToken.None); await client.ShutdownAsync(CancellationToken.None); }
            catch (IOException) { }
            return 0;
        }
        if (!AgentArguments.TryParse(args, out var options, out _))
        {
            return 1;
        }

        using var lifetime = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        };

        return await AgentRuntime.RunAsync(options, lifetime.Token).ConfigureAwait(false);
    }
}
