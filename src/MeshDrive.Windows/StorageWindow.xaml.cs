using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeshDrive.Core;
using MeshDrive.Protocol;
using Microsoft.Win32;

namespace MeshDrive.Windows;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF Closed event cancels and disposes the window lifetime.")]
public partial class StorageWindow : Window
{
    private AgentIpcClient? _client;
    private string _path = "";
    private readonly CancellationTokenSource _lifetime = new();
    private GridView? _detailsView;
    private int _navigation;
    private readonly List<(string DeviceId, string JobId)> _directCopies = [];
    private readonly Stack<string> _history = new();
    private bool _selectingTree;
    private sealed record TreeShare(IpcTrustedPeer Device, RemoteShare Share, List<RemoteShare> Shares);
    public StorageWindow()
    {
        InitializeComponent();
        _detailsView = Entries.View as GridView;
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            _client = await AgentIpcClient.ConnectAsync(IpcNames.DefaultPipeName, TimeSpan.FromSeconds(5), _lifetime.Token);
            await RefreshDevicesAsync();
            LocalShares.ItemsSource = (await SendAsync(new())).LocalShares;
            var settings = System.Text.Json.Nodes.JsonNode.Parse((await SendAsync(new() { Action = "settings" })).Value!);
            NameSetting.Text = settings?["DeviceName"]?.GetValue<string>() ?? Environment.MachineName;
            AutoStart.IsChecked = settings?["OnboardingComplete"]?.GetValue<bool>() != true || settings?["AutoStart"]?.GetValue<bool>() == true;
            if (settings?["OnboardingComplete"]?.GetValue<bool>() != true) Sections.SelectedIndex = 1;
        });
        Closed += async (_, _) => { await _lifetime.CancelAsync(); if (_client is not null) await _client.DisposeAsync(); _lifetime.Dispose(); };
    }
    private Task<StorageReply> SendAsync(StorageCommand command) => (_client ?? throw new IOException("Agent 연결을 기다리세요.")).StorageAsync(command, _lifetime.Token);
    private void Sync_Click(object sender, RoutedEventArgs e) => new SyncWindow { Owner = this }.Show();
    private void ManageDevices_Click(object sender, RoutedEventArgs e) { Owner?.Activate(); Close(); }
    private async void ReloadDevices_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshDevicesAsync);
    private async Task RefreshDevicesAsync()
    {
        if (_client is null) return;
        var devices = (await _client.GetTrustedAsync(_lifetime.Token)).Trusted ?? [];
        var peers = await _client.GetPeersAsync(_lifetime.Token);
        Devices.ItemsSource = devices; DeviceTree.Items.Clear();
        DeviceTree.Items.Add(new TreeViewItem { Header = "내 기기 · 공유 폴더", Tag = "local" });
        foreach (var device in devices)
        {
            var online = peers.Any(p => p.DeviceId == device.DeviceId && p.IsOnline);
            var node = new TreeViewItem { Header = $"{device.Name} · {(online ? "온라인" : "발견 대기")}", Tag = device };
            node.Items.Add("공유 불러오기");
            node.Expanded += async (_, e) =>
            {
                if (e.Source != node || node.Items.Count != 1 || node.Items[0] is not string) return;
                await RunAsync(async () =>
                {
                    var shares = (await SendAsync(new() { Action = "remote-shares", DeviceId = device.DeviceId })).Shares ?? [];
                    node.Items.Clear();
                    foreach (var share in shares) node.Items.Add(new TreeViewItem { Header = share.Name, Tag = new TreeShare(device, share, shares) });
                });
            };
            DeviceTree.Items.Add(node);
        }
    }
    private void TreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem node) return;
        if (node.Tag is string) { Sections.SelectedIndex = 2; return; }
        _selectingTree = true;
        try
        {
            if (node.Tag is TreeShare selected)
            {
                Devices.SelectedItem = selected.Device; RemoteShares.ItemsSource = selected.Shares; RemoteShares.SelectedItem = selected.Share;
            }
            else if (node.Tag is IpcTrustedPeer device)
            {
                ++_navigation; Devices.SelectedItem = device; RemoteShares.ItemsSource = null; Entries.ItemsSource = null; _path = ""; _history.Clear();
                Location.Text = $"{device.Name} · 왼쪽에서 공유 폴더를 선택하세요."; node.IsExpanded = true;
            }
        }
        finally { _selectingTree = false; }
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); Feedback.Text = "완료"; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Feedback.Text = e.Message; }
    }
    private async void DeviceChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(async () =>
    {
        if (_selectingTree) return;
        var generation = ++_navigation; Entries.ItemsSource = null; _path = ""; _history.Clear();
        if (Devices.SelectedItem is IpcTrustedPeer device)
        {
            var shares = (await SendAsync(new() { Action = "remote-shares", DeviceId = device.DeviceId })).Shares;
            if (generation == _navigation) RemoteShares.ItemsSource = shares;
        }
    });
    private async void ShareChanged(object sender, SelectionChangedEventArgs e) { ++_navigation; _path = ""; _history.Clear(); await RunAsync(LoadEntriesAsync); }
    private async Task LoadEntriesAsync()
    {
        if (Devices.SelectedItem is not IpcTrustedPeer device || RemoteShares.SelectedItem is not RemoteShare share) return;
        var navigation = ++_navigation;
        var rows = (await SendAsync(new() { Action = "entries", DeviceId = device.DeviceId, ShareId = share.Id, Path = _path })).Entries?.Select(e => new FileRow(e)).ToArray() ?? [];
        if (navigation != _navigation) return;
        Entries.ItemsSource = rows;
        Location.Text = $"{device.Name} / {share.Name} / {_path}";
        foreach (var row in rows.Where(r => MeshDrive.Agent.PhotoCache.IsImage(r.Name)))
        {
            if (navigation != _navigation) break;
            try
            {
                var thumbnail = await SendAsync(new() { Action = "thumbnail", DeviceId = device.DeviceId, ShareId = share.Id, Path = row.RelativePath });
                var image = new System.Windows.Media.Imaging.BitmapImage(); image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(thumbnail.Value!); image.EndInit(); image.Freeze(); row.Thumbnail = image;
            }
            catch (IOException) { }
        }
    }
    private async void EntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Entries.SelectedItem is FileRow { IsDirectory: true } entry) { _history.Push(_path); _path = entry.RelativePath; await RunAsync(LoadEntriesAsync); }
        else await RunAsync(() => OpenFileAsync(false));
    }
    private async void OpenFile_Click(object sender, RoutedEventArgs e) => await RunAsync(() => OpenFileAsync(false));
    private async void Download_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Entries.SelectedItem is not FileRow { IsDirectory: false } file || Devices.SelectedItem is not IpcTrustedPeer device || RemoteShares.SelectedItem is not RemoteShare share) return;
        var dialog = new OpenFolderDialog { Title = "파일을 저장할 폴더" };
        if (dialog.ShowDialog(this) != true) return;
        await SendAsync(new() { Action = "download", DeviceId = device.DeviceId, ShareId = share.Id, Path = file.RelativePath, Destination = dialog.FolderName });
    });
    private async void Upload_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Devices.SelectedItem is not IpcTrustedPeer device || RemoteShares.SelectedItem is not RemoteShare share) return;
        var dialog = new OpenFileDialog { Title = "상대 폴더에 복사할 파일", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) await SendAsync(new() { Action = "upload", DeviceId = device.DeviceId, ShareId = share.Id, Path = file, Destination = _path });
    });
    private async void Transfers_Click(object sender, RoutedEventArgs e)
    {
        try {
            var result = await SendAsync(new() { Action = "transfers" });
            var transfers = result.Transfers ?? [];
            foreach (var copy in _directCopies)
            {
                try { transfers.AddRange((await SendAsync(new() { Action = "copy-progress", DeviceId = copy.DeviceId, Path = copy.JobId })).Transfers ?? []); }
                catch (IOException) { transfers.Add(new(copy.JobId, "직접 복사", 0, 0, "대상 기기 상태 확인 불가", null, null)); }
            }
            Feedback.Text = string.Join("\n", transfers.Select(t => $"{t.Name} · {t.State} · {t.CompletedBytes:N0}/{t.TotalBytes:N0} bytes · {t.Result ?? t.Error}"));
        }
        catch (IOException error) { Feedback.Text = error.Message; }
    }
    private async void DirectCopy_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Entries.SelectedItem is not FileRow { IsDirectory: false } file || Devices.SelectedItem is not IpcTrustedPeer source || RemoteShares.SelectedItem is not RemoteShare share) return;
        var dialog = new CopyTargetWindow(Devices.Items.Cast<IpcTrustedPeer>().Where(d => d.DeviceId != source.DeviceId), SendAsync) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.TargetDevice?.DeviceId is not { } targetId || dialog.TargetShare is not { } destination) return;
        var result = await SendAsync(new() { Action = "copy-direct", DeviceId = source.DeviceId, ShareId = share.Id, Path = file.RelativePath,
            TargetDeviceId = targetId, TargetShareId = destination.Id, Destination = dialog.TargetPath });
        if (result.Value is not null) _directCopies.Add((targetId, result.Value));
    });
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await SendAsync(new() { Action = "save-settings", Name = NameSetting.Text, Permissions = AutoStart.IsChecked == true ? SharePermissions.All : SharePermissions.None }));
    private async void Pause_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await SendAsync(new() { Action = "pause" }));
    private async void Resume_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => await SendAsync(new() { Action = "resume" }));
    private async void OpenWith_Click(object sender, RoutedEventArgs e) => await RunAsync(() => OpenFileAsync(true));
    private void MusicPlayer_Click(object sender, RoutedEventArgs e) => ConfigurePlayer(true);
    private void VideoPlayer_Click(object sender, RoutedEventArgs e) => ConfigurePlayer(false);
    private string? ConfigurePlayer(bool music)
    {
        var dialog = new OpenFileDialog { Filter = "플레이어 실행 파일 (*.exe)|*.exe", Title = music ? "음악 플레이어 선택" : "영상 플레이어 선택" };
        if (dialog.ShowDialog(this) != true) return null;
        var preferences = PlayerPreferences.Load(AppPaths.DefaultDataDirectory);
        if (music) preferences.MusicPlayer = dialog.FileName; else preferences.VideoPlayer = dialog.FileName;
        preferences.Save(AppPaths.DefaultDataDirectory); return dialog.FileName;
    }
    private async Task OpenFileAsync(bool choosePlayer)
    {
        if (Entries.SelectedItem is not FileRow { IsDirectory: false } entry || Devices.SelectedItem is not IpcTrustedPeer device || RemoteShares.SelectedItem is not RemoteShare share) return;
        if (MeshDrive.Agent.PhotoCache.IsImage(entry.Name))
        {
            var photo = await SendAsync(new() { Action = "open-photo", DeviceId = device.DeviceId, ShareId = share.Id, Path = entry.RelativePath });
            if (choosePlayer)
            {
                var dialog = new OpenFileDialog { Filter = "프로그램 (*.exe)|*.exe", Title = "사진을 열 프로그램 선택" };
                if (dialog.ShowDialog(this) != true) return;
                var photoStart = new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = false };
                photoStart.ArgumentList.Add(photo.Value!);
                System.Diagnostics.Process.Start(photoStart)?.Dispose(); return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(photo.Value!) { UseShellExecute = true })?.Dispose(); return;
        }
        var music = PlayerPreferences.IsMusic(entry.Name);
        var preferences = PlayerPreferences.Load(AppPaths.DefaultDataDirectory);
        var player = music ? preferences.MusicPlayer : preferences.VideoPlayer;
        if (choosePlayer || string.IsNullOrEmpty(player) || !File.Exists(player)) player = ConfigurePlayer(music);
        if (player is null) return;
        var stream = await SendAsync(new() { Action = "open-stream", DeviceId = device.DeviceId, ShareId = share.Id, Path = entry.RelativePath });
        var start = new System.Diagnostics.ProcessStartInfo(player) { UseShellExecute = false };
        start.ArgumentList.Add(stream.Value!);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
    private void ViewChanged(object sender, RoutedEventArgs e)
    {
        if (Entries is null) return;
        if (LargeIcons.IsChecked == true)
        {
            Entries.View = null;
            Entries.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Width='170' Margin='8'><Image Source='{Binding Thumbnail}' Width='150' Height='120'/><TextBlock Text='{Binding Name}' TextWrapping='Wrap'/></StackPanel></DataTemplate>");
            Entries.ItemsPanel = (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><WrapPanel/></ItemsPanelTemplate>");
        }
        else { Entries.ItemTemplate = null; Entries.ClearValue(ItemsControl.ItemsPanelProperty); Entries.View = _detailsView; }
    }
    private async void Back_Click(object sender, RoutedEventArgs e) { if (_history.TryPop(out var previous)) { _path = previous; await RunAsync(LoadEntriesAsync); } }
    private async void Parent_Click(object sender, RoutedEventArgs e) { if (_path.Length == 0) return; _history.Push(_path); var index = _path.LastIndexOf('/'); _path = index < 0 ? "" : _path[..index]; await RunAsync(LoadEntriesAsync); }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadEntriesAsync);
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) { LocalShares.SelectedItem = null; FolderPath.Text = dialog.FolderName; Alias.Text = Path.GetFileName(dialog.FolderName); }
    }
    private async void SaveShare_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var current = LocalShares.SelectedItem as SharedFolder;
        LocalShares.ItemsSource = (await SendAsync(new() { Action = "save-share", ShareId = current?.Id, Path = FolderPath.Text, Name = Alias.Text,
            Permissions = Preset.SelectedIndex switch { 1 => SharePermissions.All, 2 => SharePermissions.Browse | SharePermissions.Stream, _ => SharePermissions.ReadOnly }, DeviceOverrides = current?.DeviceOverrides })).LocalShares;
    });
    private async void RemoveShare_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (LocalShares.SelectedItem is SharedFolder share) LocalShares.ItemsSource = (await SendAsync(new() { Action = "remove-share", ShareId = share.Id })).LocalShares;
    });
    private async void Permissions_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (LocalShares.SelectedItem is not SharedFolder share) return;
        var dialog = new SharePermissionsWindow(share, Devices.Items.Cast<IpcTrustedPeer>()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        LocalShares.ItemsSource = (await SendAsync(new() { Action = "save-share", ShareId = share.Id, Name = share.Name, Path = share.LocalPath, Permissions = share.Permissions, DeviceOverrides = dialog.Overrides })).LocalShares;
    });
    private void LocalShareChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalShares.SelectedItem is not SharedFolder share) return;
        FolderPath.Text = share.LocalPath; Alias.Text = share.Name;
        Preset.SelectedIndex = share.Permissions == SharePermissions.All ? 1 : share.Permissions == (SharePermissions.Browse | SharePermissions.Stream) ? 2 : 0;
    }
}

public sealed class FileRow(RemoteEntry entry) : System.ComponentModel.INotifyPropertyChanged
{
    public string Icon => entry.IsDirectory ? "📁" : "📄";
    public string Name => entry.Name;
    public string RelativePath => entry.RelativePath;
    public bool IsDirectory => entry.IsDirectory;
    public long Length => entry.Length;
    public DateTimeOffset ModifiedAt => entry.ModifiedAt;
    private System.Windows.Media.ImageSource? _thumbnail;
    public System.Windows.Media.ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
