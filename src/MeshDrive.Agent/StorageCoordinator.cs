using MeshDrive.Core;
using MeshDrive.Protocol;
using System.Net.Http.Json;

namespace MeshDrive.Agent;

public sealed class StorageCoordinator(StorageService storage, RemoteStorageClient remote)
{
    public LocalStreamBridge? Bridge { get; init; }
    public RemotePhotoService? Photos { get; init; }
    public FileTransferService? Transfers { get; init; }
    public AgentSettings? Settings { get; init; }
    public string? DataDirectory { get; init; }
    public SyncCoordinator? Sync { get; init; }
    public async Task<IpcMessage?> HandleAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        if (message.Type != "storage" || message.Storage is not { } command) return null;
        try
        {
            if (command.Action.StartsWith("sync-", StringComparison.Ordinal)) return new() { Type = "storage-result", StorageResult = await (Sync ?? throw new IOException("동기화가 준비되지 않았습니다.")).HandleAsync(command, cancellationToken).ConfigureAwait(false) };
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
                case "copy-direct":
                    using (var authorized = await remote.SendAsync(command.DeviceId!, HttpMethod.Post, "/v1/secure/storage/copy-authorize",
                        r => r.Content = JsonContent.Create(new CopyGrantRequest(command.TargetDeviceId!, command.ShareId!, command.Path)), cancellationToken).ConfigureAwait(false))
                    {
                        authorized.EnsureSuccessStatusCode();
                        var ticket = await authorized.Content.ReadFromJsonAsync<CopyTicket>(cancellationToken).ConfigureAwait(false) ?? throw new IOException("복사 권한 응답이 없습니다.");
                        using var receive = await remote.SendAsync(command.TargetDeviceId!, HttpMethod.Post, "/v1/secure/storage/copy-receive",
                            r => r.Content = JsonContent.Create(new CopyReceiveRequest(command.DeviceId!, ticket.Token, command.TargetShareId!, command.Destination ?? "")), cancellationToken).ConfigureAwait(false);
                        receive.EnsureSuccessStatusCode();
                        result.Value = (await receive.Content.ReadFromJsonAsync<CopyJob>(cancellationToken).ConfigureAwait(false))?.Id;
                    }
                    break;
                case "copy-progress": result.Transfers = [await remote.GetAsync<TransferProgress>(command.DeviceId!, "/v1/secure/storage/copy-progress?id=" + Uri.EscapeDataString(command.Path), cancellationToken).ConfigureAwait(false)]; break;
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
