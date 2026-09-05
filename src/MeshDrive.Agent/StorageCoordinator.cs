using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class StorageCoordinator(StorageService storage, RemoteStorageClient remote)
{
    public LocalStreamBridge? Bridge { get; init; }
    public RemotePhotoService? Photos { get; init; }
    public FileTransferService? Transfers { get; init; }
    public AgentSettings? Settings { get; init; }
    public string? DataDirectory { get; init; }
    public async Task<IpcMessage?> HandleAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        if (message.Type != "storage" || message.Storage is not { } command) return null;
        try
        {
            var result = new StorageReply();
            switch (command.Action)
            {
                case "local-shares": result.LocalShares = storage.Shares.Snapshot().ToList(); break;
                case "save-share":
                    storage.Shares.Save(command.ShareId, command.Name, command.Path, command.Permissions, command.DeviceOverrides);
                    result.LocalShares = storage.Shares.Snapshot().ToList(); break;
                case "remove-share": storage.Shares.Remove(command.ShareId!); result.LocalShares = storage.Shares.Snapshot().ToList(); break;
                case "remote-shares": result.Shares = await remote.GetAsync<List<RemoteShare>>(command.DeviceId!, "/v1/secure/storage/shares", cancellationToken).ConfigureAwait(false); break;
                case "entries": result.Entries = await remote.GetAsync<List<RemoteEntry>>(command.DeviceId!, RemoteStorageClient.Resource("entries", command.ShareId!, command.Path), cancellationToken).ConfigureAwait(false); break;
                case "open-stream": result.Value = await (Bridge ?? throw new IOException("재생 브리지가 준비되지 않았습니다.")).CreateAsync(command.DeviceId!, command.ShareId!, command.Path, cancellationToken).ConfigureAwait(false); break;
                case "close-stream": Bridge?.Revoke(command.Path); break;
                case "open-photo": case "thumbnail":
                    result.Value = await (Photos ?? throw new IOException("사진 캐시가 준비되지 않았습니다.")).GetAsync(command.DeviceId!, command.ShareId!, command.Path, command.Action == "thumbnail", cancellationToken).ConfigureAwait(false); break;
                case "download": case "upload": result.Value = (Transfers ?? throw new IOException("전송 엔진이 준비되지 않았습니다.")).Start(command); break;
                case "transfers": result.Transfers = Transfers?.Progress.ToList() ?? []; break;
                case "settings": result.Value = System.Text.Json.JsonSerializer.Serialize(new { Settings?.DeviceName, Settings?.OnboardingComplete, SharingPaused = storage.Paused, AutoStart = AgentSettings.AutoStartEnabled() }); break;
                case "save-settings":
                    if (Settings is null || DataDirectory is null) throw new IOException("설정이 준비되지 않았습니다.");
                    Settings.DeviceName = DeviceIdentityStore.NormalizeName(command.Name);
                    Settings.OnboardingComplete = true; Settings.Save(DataDirectory);
                    AgentSettings.SetAutoStart(command.Permissions != SharePermissions.None);
                    result.Value = "기기 이름은 다음 Agent 시작부터 적용됩니다."; break;
                case "pause": case "resume":
                    storage.Paused = command.Action == "pause";
                    if (Settings is not null && DataDirectory is not null) { Settings.SharingPaused = storage.Paused; Settings.Save(DataDirectory); } break;
                default: throw new ArgumentException("지원하지 않는 저장소 명령입니다.");
            }
            return new IpcMessage { Type = "storage-result", StorageResult = result };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException or System.Text.Json.JsonException)
        {
            return new IpcMessage { Type = IpcProtocol.TypeError, Error = e.Message };
        }
    }
}
