using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace MeshDrive.Agent;

public sealed class TrayFolderConnection(Func<bool> paused, Action<bool> setPaused, Func<int> onlineCount, Action shutdown) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    public void Start() => _loop = Task.Run(RunAsync);
    public static string PipeName => "eslee.trayfolder.tray-host.v1." + new string(Environment.UserName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
    private static void Activate() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MeshDrive.Windows.exe")) { UseShellExecute = false })?.Dispose();
    private async Task RunAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(1500, token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { type = "register", protocolVersion = 1, appId = "eslee.meshdrive", displayName = "MeshDrive", processId = Environment.ProcessId, mode = "standalone" }).AsMemory(), token).ConfigureAwait(false);
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    var request = JsonNode.Parse(line); var id = request?["id"]?.GetValue<int>();
                    object? response = null;
                    if (request?["type"]?.GetValue<string>() == "get-menu") response = new { type = "menu", id, items = new[] {
                        new { id = "open", text = "MeshDrive 열기", enabled = true },
                        new { id = "status", text = $"{(paused() ? "공유 중지" : "공유 중")} · 온라인 기기 {onlineCount()}대", enabled = false },
                        new { id = "pause", text = paused() ? "공유 다시 시작" : "공유 일시 중지", enabled = true },
                        new { id = "quit", text = "MeshDrive 전체 종료", enabled = true } } };
                    if (request?["type"]?.GetValue<string>() == "command")
                    {
                        var action = request?["command"]?.GetValue<string>() == "activate" ? "open" : request?["actionId"]?.GetValue<string>();
                        if (action == "open") Activate();
                        else if (action == "pause") setPaused(!paused());
                        response = new { type = "command-result", id, succeeded = action is "open" or "pause" or "quit" };
                        if (action == "quit")
                        {
                            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(response).AsMemory(), token).ConfigureAwait(false);
                            shutdown(); return;
                        }
                    }
                    if (response is not null) await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(response).AsMemory(), token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
            try { await Task.Delay(3000, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
    public async ValueTask DisposeAsync() { await _lifetime.CancelAsync().ConfigureAwait(false); if (_loop is not null) await _loop.ConfigureAwait(false); _lifetime.Dispose(); }
}
