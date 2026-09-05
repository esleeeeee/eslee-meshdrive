using System.Text.Json;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class SyncCoordinator(SyncFolders folders, SyncRunner runner, RemoteStorageClient remote)
{
    public async Task<StorageReply> HandleAsync(StorageCommand command, CancellationToken token)
    {
        object? value = null;
        switch (command.Action)
        {
            case "sync-state": value = new SyncState(folders.Snapshot(), runner.Jobs, runner.Status, folders.VersionsPerFile, folders.RetentionDays); break;
            case "sync-save-root": value = folders.Save(command.ShareId, command.Name, command.Path, command.AllowedDevices ?? []); break;
            case "sync-remove-root":
                foreach (var job in runner.Jobs.Where(j => j.LocalRootId == command.ShareId)) runner.Remove(job.Id);
                folders.Remove(command.ShareId!); break;
            case "sync-save-job": runner.Save(command.SyncJob ?? throw new ArgumentException("동기화 작업이 없습니다.")); break;
            case "sync-remove-job": runner.Remove(command.Path); break;
            case "sync-run": await runner.RunAsync(command.Path, token).ConfigureAwait(false); value = runner.Status; break;
            case "sync-remote-roots": value = await remote.GetAsync<List<RemoteSyncFolder>>(command.DeviceId!, "/v1/secure/sync/roots", token).ConfigureAwait(false); break;
            case "sync-versions": value = folders.Versions(command.ShareId!); break;
            case "sync-restore": folders.Restore(command.ShareId!, command.Path); break;
            case "sync-retention": folders.ConfigureRetention(command.VersionCount, command.RetentionDays); break;
            default: throw new ArgumentException("지원하지 않는 동기화 명령입니다.");
        }
        return new() { Value = JsonSerializer.Serialize(value) };
    }
}
