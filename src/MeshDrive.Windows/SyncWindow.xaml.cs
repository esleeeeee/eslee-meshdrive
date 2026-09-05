using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeshDrive.Core;
using MeshDrive.Protocol;
using Microsoft.Win32;

namespace MeshDrive.Windows;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Window Closed cancels and disposes its IPC client.")]
public partial class SyncWindow : Window
{
    private AgentIpcClient? _client;
    private readonly CancellationTokenSource _lifetime = new();
    private SyncState? _state;
    private IpcTrustedPeer[] _devices = [];
    private string? _editingJob;
    private int _navigation;
    public SyncWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            _client = await AgentIpcClient.ConnectAsync(IpcNames.DefaultPipeName, TimeSpan.FromSeconds(5), _lifetime.Token);
            _devices = (await _client.GetTrustedAsync(_lifetime.Token)).Trusted?.ToArray() ?? [];
            Allowed.ItemsSource = _devices; Device.ItemsSource = _devices; await RefreshAsync();
        });
        Closed += async (_, _) => { await _lifetime.CancelAsync(); if (_client is not null) await _client.DisposeAsync(); _lifetime.Dispose(); };
    }
    private async Task<string> SendAsync(StorageCommand command) => (await (_client ?? throw new IOException("Agent 준비 중입니다.")).StorageAsync(command, _lifetime.Token)).Value ?? "null";
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); Feedback.Text = "완료"; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Feedback.Text = e.Message; }
    }
    private async Task RefreshAsync()
    {
        _state = JsonSerializer.Deserialize<SyncState>(await SendAsync(new() { Action = "sync-state" }))!;
        Roots.ItemsSource = _state.Folders; LocalRoot.ItemsSource = _state.Folders; VersionRoot.ItemsSource = _state.Folders;
        Jobs.ItemsSource = _state.Jobs.Select(j => new JobRow(j,
            _state.Folders.FirstOrDefault(f => f.Id == j.LocalRootId)?.Name ?? "해제된 폴더",
            _devices.FirstOrDefault(d => d.DeviceId == j.DeviceId)?.Name ?? "오프라인 기기",
            !j.Enabled ? "일시 중지" : _state.Status.FirstOrDefault(s => s.Id == j.Id)?.State ?? "다음 검사 대기")).ToArray();
        VersionCount.Text = _state.VersionCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetentionDays.Text = _state.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "수정·삭제 동기화를 명시적으로 허용할 폴더" };
        if (picker.ShowDialog(this) == true) { Roots.SelectedItem = null; FolderPath.Text = picker.FolderName; Alias.Text = Path.GetFileName(picker.FolderName); Allowed.UnselectAll(); }
    }
    private void RootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Roots.SelectedItem is not SyncFolder root) return;
        FolderPath.Text = root.LocalPath; Alias.Text = root.Name; Allowed.UnselectAll();
        foreach (var device in _devices.Where(d => root.AllowedDevices.Contains(d.DeviceId!))) Allowed.SelectedItems.Add(device);
    }
    private async void SaveRoot_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (MessageBox.Show(this, "선택한 기기가 이 폴더에서 파일을 생성·수정·삭제하는 동기화를 허용할까요? 일반 읽기 전용 공유와는 별개입니다.", "동기화 허용", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SendAsync(new() { Action = "sync-save-root", ShareId = (Roots.SelectedItem as SyncFolder)?.Id, Name = Alias.Text, Path = FolderPath.Text, AllowedDevices = Allowed.SelectedItems.Cast<IpcTrustedPeer>().Select(d => d.DeviceId!).ToList() });
        await RefreshAsync();
    });
    private async void RemoveRoot_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Roots.SelectedItem is not SyncFolder root) return;
        await SendAsync(new() { Action = "sync-remove-root", ShareId = root.Id }); await RefreshAsync();
    });
    private async void DeviceChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(async () =>
    {
        var generation = ++_navigation; RemoteRoot.ItemsSource = null;
        if (Device.SelectedItem is not IpcTrustedPeer device) return;
        var roots = JsonSerializer.Deserialize<List<RemoteSyncFolder>>(await SendAsync(new() { Action = "sync-remote-roots", DeviceId = device.DeviceId }));
        if (generation == _navigation) RemoteRoot.ItemsSource = roots;
    });
    private void NewJob_Click(object sender, RoutedEventArgs e) { _editingJob = null; Jobs.SelectedItem = null; Enabled.IsChecked = true; }
    private async void JobChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(async () =>
    {
        if (Jobs.SelectedItem is not JobRow row) return;
        _editingJob = row.Job.Id; LocalRoot.SelectedItem = _state?.Folders.FirstOrDefault(f => f.Id == row.Job.LocalRootId);
        Device.SelectedItem = _devices.FirstOrDefault(d => d.DeviceId == row.Job.DeviceId);
        Mode.SelectedIndex = (int)row.Job.Mode; Enabled.IsChecked = row.Job.Enabled;
        var roots = JsonSerializer.Deserialize<List<RemoteSyncFolder>>(await SendAsync(new() { Action = "sync-remote-roots", DeviceId = row.Job.DeviceId }));
        RemoteRoot.ItemsSource = roots; RemoteRoot.SelectedItem = roots?.FirstOrDefault(r => r.Id == row.Job.RemoteRootId);
    });
    private async void SaveJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (LocalRoot.SelectedItem is not SyncFolder local || Device.SelectedItem is not IpcTrustedPeer device || RemoteRoot.SelectedItem is not RemoteSyncFolder remote) throw new IOException("양쪽 폴더와 기기를 선택하세요.");
        if (MessageBox.Show(this, "이 규칙에 따라 파일 생성·수정·삭제를 자동 반영할까요? 충돌은 사본으로, 교체·삭제 전 파일은 이전 버전으로 보관합니다.", "동기화 규칙 저장", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SendAsync(new() { Action = "sync-save-job", SyncJob = new(_editingJob ?? Guid.NewGuid().ToString("N"), local.Id, device.DeviceId!, remote.Id, (SyncMode)Mode.SelectedIndex, Enabled.IsChecked == true) });
        _editingJob = null; await RefreshAsync();
    });
    private async void RemoveJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { if (Jobs.SelectedItem is JobRow row) await SendAsync(new() { Action = "sync-remove-job", Path = row.Job.Id }); _editingJob = null; await RefreshAsync(); });
    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { if (Jobs.SelectedItem is JobRow row) await SendAsync(new() { Action = "sync-run", Path = row.Job.Id }); await RefreshAsync(); });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void VersionRootChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(async () => { if (VersionRoot.SelectedItem is SyncFolder root) Versions.ItemsSource = JsonSerializer.Deserialize<List<SyncVersion>>(await SendAsync(new() { Action = "sync-versions", ShareId = root.Id })); });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (VersionRoot.SelectedItem is not SyncFolder root || Versions.SelectedItem is not SyncVersion version) return;
        if (MessageBox.Show(this, "선택한 이전 버전으로 복원할까요? 현재 파일도 이전 버전으로 보관됩니다. 활성 동기화 규칙은 복원된 변경을 다른 기기로 반영합니다.", "버전 복원", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SendAsync(new() { Action = "sync-restore", ShareId = root.Id, Path = version.Id }); await RefreshAsync();
    });
    private async void Retention_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!int.TryParse(VersionCount.Text, out var count) || !int.TryParse(RetentionDays.Text, out var days)) throw new IOException("보관 개수와 일수를 숫자로 입력하세요.");
        await SendAsync(new() { Action = "sync-retention", VersionCount = count, RetentionDays = days }); await RefreshAsync();
    });
}

public sealed record JobRow(SyncJob Job, string LocalName, string DeviceName, string State)
{
    public string Direction => Job.Mode switch { SyncMode.Push => "내 폴더 → 상대", SyncMode.Pull => "상대 → 내 폴더", _ => "양방향" };
}
