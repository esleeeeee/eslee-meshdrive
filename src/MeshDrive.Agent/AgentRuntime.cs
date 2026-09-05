using MeshDrive.Core;
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

        MdnsDiscoveryHost? mdns = null;
        AgentHttpsHost? https = null;
        try
        {
            var identity = DeviceIdentityStore.LoadOrCreate(options.DataDirectory);
            var credential = DeviceCredentialStore.LoadOrCreate(options.DataDirectory, identity.DeviceId);
            var trust = new TrustedPeerStore(options.DataDirectory);
            var directory = new PeerDirectory(identity.DeviceId, DiscoveryNames.OfflineAfter);
            var coordinator = new PairingCoordinator(
                identity,
                credential,
                trust,
                directory,
                options.HttpsPort);
            var discovery = DiscoveryNames.DiscoveryOff;
            var storage = new StorageService(new SharedFolderStore(options.DataDirectory));
            var storageCommands = new StorageCoordinator(storage, new RemoteStorageClient(credential, coordinator));
            await using var server = new AgentIpcServer(
                options.PipeName,
                DateTimeOffset.Now,
                deviceId: identity.DeviceId,
                deviceName: identity.DeviceName,
                discovery: DiscoveryNames.DiscoveryOff,
                listPeers: coordinator.ListPeers,
                handleCommand: async (message, token) => await storageCommands.HandleAsync(message, token).ConfigureAwait(false)
                    ?? await coordinator.HandleIpcAsync(message, token).ConfigureAwait(false),
                discoveryProvider: () => discovery);
            var ipc = server.RunAsync(cancellationToken);
            if (options.EnableHttps)
            {
                https = new AgentHttpsHost(identity, credential, coordinator, options.HttpsPort) { Storage = storage };
                try
                {
                    await https.TryStartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            if (options.EnableMdns)
            {
                mdns = new MdnsDiscoveryHost(identity, directory, options.HttpsPort);
                if (mdns.TryStart())
                {
                    discovery = DiscoveryNames.DiscoveryMdns;
                }
            }

            await ipc.ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (https is not null)
            {
                await https.DisposeAsync().ConfigureAwait(false);
            }

            mdns?.Dispose();
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
