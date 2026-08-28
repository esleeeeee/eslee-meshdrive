namespace MeshDrive.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
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
