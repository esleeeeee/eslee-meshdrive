using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public static class AgentRuntime
{
    public static async Task<int> RunAsync(AgentHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryAcquireMutex(options.MutexName, out var mutex, out var ownsMutex))
        {
            return 0;
        }

        try
        {
            await using var server = new AgentIpcServer(options.PipeName, DateTimeOffset.Now);
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (ownsMutex && mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            mutex?.Dispose();
        }
    }

    private static bool TryAcquireMutex(string mutexName, out Mutex? mutex, out bool ownsMutex)
    {
        mutex = null;
        ownsMutex = false;
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            return true;
        }

        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out ownsMutex);
            if (ownsMutex)
            {
                return true;
            }

            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch (AbandonedMutexException abandoned)
        {
            mutex = abandoned.Mutex;
            ownsMutex = true;
            return true;
        }
    }
}
