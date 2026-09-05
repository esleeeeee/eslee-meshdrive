using System.Collections.Concurrent;
using System.Text.Json;
using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed class SyncRunner : IAsyncDisposable
{
    private readonly SyncFolders _folders;
    private readonly SyncTransport _transport;
    private readonly string _directory;
    private readonly string _settings;
    private readonly List<SyncJob> _jobs;
    private readonly object _settingsGate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, SyncJobStatus> _status = new(StringComparer.Ordinal);
    private Task? _loop;
    public Func<bool> IsPaused { private get; set; } = () => false;
    public Func<string, bool> IsTrusted { private get; set; } = _ => false;

    public SyncRunner(SyncFolders folders, SyncTransport transport, string dataDirectory)
    {
        _folders = folders; _transport = transport; _directory = Path.Combine(dataDirectory, "sync-state"); Directory.CreateDirectory(_directory);
        _settings = Path.Combine(dataDirectory, "sync-jobs.json");
        _jobs = File.Exists(_settings) ? JsonSerializer.Deserialize<List<SyncJob>>(File.ReadAllText(_settings)) ?? [] : [];
    }
    public IReadOnlyList<SyncJob> Jobs { get { lock (_settingsGate) return _jobs.ToArray(); } }
    public IReadOnlyList<SyncJobStatus> Status => _status.Values.ToArray();
    public void Save(SyncJob job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _) || !Enum.IsDefined(job.Mode)) throw new ArgumentException("잘못된 동기화 작업입니다.", nameof(job));
        _folders.Require(job.LocalRootId, job.DeviceId);
        lock (_settingsGate)
        {
            if (_jobs.Any(j => j.Id != job.Id && j.LocalRootId == job.LocalRootId && j.DeviceId == job.DeviceId && j.RemoteRootId == job.RemoteRootId)) throw new ArgumentException("이미 등록된 동기화입니다.", nameof(job));
            var next = _jobs.Where(j => j.Id != job.Id).Append(job).ToList();
            Persist(_settings, next); _jobs.Clear(); _jobs.AddRange(next);
        }
    }
    public void Remove(string id)
    {
        lock (_settingsGate) { var next = _jobs.Where(j => j.Id != id).ToList(); Persist(_settings, next); _jobs.Clear(); _jobs.AddRange(next); }
    }
    public void Start() => _loop ??= Task.Run(async () =>
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                foreach (var job in Jobs.Where(j => j.Enabled)) await RunAsync(job.Id, _lifetime.Token).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    });
    private void RequireActive(SyncJob job)
    {
        if (IsPaused() || !IsTrusted(job.DeviceId) || !Jobs.Any(j => j == job && j.Enabled)) throw new UnauthorizedAccessException("동기화가 중지되었거나 기기 신뢰가 해제되었습니다.");
        _folders.Require(job.LocalRootId, job.DeviceId);
    }
    public async Task RunAsync(string id, CancellationToken token)
    {
        await _runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var job = Jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return;
            var conflicts = 0;
            try
            {
                RequireActive(job); _status[id] = new(id, "검사 중", null, 0, null);
                var statePath = Path.Combine(_directory, job.Id + ".json");
                var baseline = File.Exists(statePath) ? JsonSerializer.Deserialize<Dictionary<string, SyncBaseline>>(File.ReadAllText(statePath)) ?? [] : [];
                var left = _folders.Inventory(job.LocalRootId, job.DeviceId).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
                var right = (await _transport.InventoryAsync(job.DeviceId, job.RemoteRootId, token).ConfigureAwait(false)).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var path in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).Union(baseline.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    token.ThrowIfCancellationRequested(); RequireActive(job);
                    var a = left.GetValueOrDefault(path); var b = right.GetValueOrDefault(path);
                    if (_folders.CurrentHash(job.LocalRootId, path, job.DeviceId) != a?.Hash) continue;
                    var action = SyncPlanner.Decide(a?.Hash, b?.Hash, baseline.GetValueOrDefault(path), job.Mode);
                    if (action == SyncAction.Conflict)
                    {
                        conflicts++;
                        if (a is not null) await PreserveConflictAsync(job, a, _folders.Resolve(job.LocalRootId, path, job.DeviceId), right, token).ConfigureAwait(false);
                        if (b is not null)
                        {
                            var cached = await _transport.DownloadAsync(job.DeviceId, job.RemoteRootId, b, token, () => RequireActive(job)).ConfigureAwait(false);
                            try { await PreserveConflictAsync(job, b, cached, right, token).ConfigureAwait(false); }
                            finally { _transport.Release(cached); }
                        }
                        action = job.Mode switch
                        {
                            SyncMode.Push => a is null ? SyncAction.DeleteRight : SyncAction.CopyLeftToRight,
                            SyncMode.Pull => b is null ? SyncAction.DeleteLeft : SyncAction.CopyRightToLeft,
                            _ => SyncAction.None,
                        };
                    }
                    RequireActive(job);
                    switch (action)
                    {
                        case SyncAction.CopyLeftToRight:
                            await _transport.UploadAsync(job.DeviceId, job.RemoteRootId, path, b?.Hash, _folders.Resolve(job.LocalRootId, path, job.DeviceId), a!.Hash, token, () => RequireActive(job)).ConfigureAwait(false);
                            b = a; break;
                        case SyncAction.CopyRightToLeft:
                            var file = await _transport.DownloadAsync(job.DeviceId, job.RemoteRootId, b!, token, () => RequireActive(job)).ConfigureAwait(false);
                            try { RequireActive(job); _folders.Apply(job.LocalRootId, path, a?.Hash, file, b!.Hash, job.DeviceId); a = b; }
                            finally { _transport.Release(file); }
                            break;
                        case SyncAction.DeleteLeft: _folders.Apply(job.LocalRootId, path, a!.Hash, null, null, job.DeviceId); a = null; break;
                        case SyncAction.DeleteRight:
                            await _transport.DeleteAsync(job.DeviceId, job.RemoteRootId, path, b!.Hash, token).ConfigureAwait(false); b = null; break;
                    }
                    baseline[path] = new(a?.Hash, b?.Hash); Persist(statePath, baseline);
                }
                _status[id] = new(id, conflicts == 0 ? "동기화 완료" : "충돌 사본 보존 · 원본을 확인하세요", DateTimeOffset.UtcNow, conflicts, null);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or ArgumentException or JsonException)
            { _status[id] = new(id, "대기 · 다음 연결에서 다시 시도", DateTimeOffset.UtcNow, conflicts, e.Message); }
        }
        finally { _runGate.Release(); }
    }
    private async Task PreserveConflictAsync(SyncJob job, SyncEntry entry, string source, Dictionary<string, SyncEntry> remote, CancellationToken token)
    {
        RequireActive(job);
        var path = SyncPlanner.ConflictPath(entry.Path, entry.Hash);
        var localHash = _folders.CurrentHash(job.LocalRootId, path, job.DeviceId);
        var remoteHash = remote.GetValueOrDefault(path)?.Hash;
        if ((localHash is not null && localHash != entry.Hash) || (remoteHash is not null && remoteHash != entry.Hash)) throw new IOException("기존 충돌 사본이 수정되었습니다. 확인 후 동기화를 다시 실행하세요.");
        if (localHash is null) _folders.Apply(job.LocalRootId, path, null, source, entry.Hash, job.DeviceId);
        if (remoteHash is null)
        {
            await _transport.UploadAsync(job.DeviceId, job.RemoteRootId, path, null, source, entry.Hash, token, () => RequireActive(job)).ConfigureAwait(false);
            remote[path] = new(path, entry.Size, entry.Hash);
        }
    }
    private static void Persist<T>(string path, T value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value)); File.Move(path + ".tmp", path, true);
    }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await _runGate.WaitAsync().ConfigureAwait(false); _runGate.Release(); _runGate.Dispose(); _lifetime.Dispose();
    }
}
