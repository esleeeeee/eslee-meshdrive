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
    public StorageWindow()
    {
        InitializeComponent();
        _detailsView = Entries.View as GridView;
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            _client = await AgentIpcClient.ConnectAsync(IpcNames.DefaultPipeName, TimeSpan.FromSeconds(5), _lifetime.Token);
            Devices.ItemsSource = (await _client.GetTrustedAsync(_lifetime.Token)).Trusted;
            LocalShares.ItemsSource = (await SendAsync(new())).LocalShares;
        });
        Closed += async (_, _) => { await _lifetime.CancelAsync(); if (_client is not null) await _client.DisposeAsync(); _lifetime.Dispose(); };
    }
    private Task<StorageReply> SendAsync(StorageCommand command) => (_client ?? throw new IOException("Agent 연결을 기다리세요.")).StorageAsync(command, _lifetime.Token);
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); Feedback.Text = "완료"; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Feedback.Text = e.Message; }
    }
    private async void DeviceChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(async () =>
    {
        Entries.ItemsSource = null; _path = "";
        if (Devices.SelectedItem is IpcTrustedPeer device)
            RemoteShares.ItemsSource = (await SendAsync(new() { Action = "remote-shares", DeviceId = device.DeviceId })).Shares;
    });
    private async void ShareChanged(object sender, SelectionChangedEventArgs e) { _path = ""; await RunAsync(LoadEntriesAsync); }
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
        if (Entries.SelectedItem is FileRow { IsDirectory: true } entry) { _path = entry.RelativePath; await RunAsync(LoadEntriesAsync); }
        else await RunAsync(() => OpenFileAsync(false));
    }
    private async void OpenFile_Click(object sender, RoutedEventArgs e) => await RunAsync(() => OpenFileAsync(false));
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
    private async void Parent_Click(object sender, RoutedEventArgs e) { var index = _path.LastIndexOf('/'); _path = index < 0 ? "" : _path[..index]; await RunAsync(LoadEntriesAsync); }
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
    private void LocalShareChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalShares.SelectedItem is not SharedFolder share) return;
        FolderPath.Text = share.LocalPath; Alias.Text = share.Name;
        Preset.SelectedIndex = share.Permissions == SharePermissions.All ? 1 : share.Permissions == (SharePermissions.Browse | SharePermissions.Stream) ? 2 : 0;
    }
}

public sealed class FileRow(RemoteEntry entry) : System.ComponentModel.INotifyPropertyChanged
{
    public string Name => entry.Name;
    public string RelativePath => entry.RelativePath;
    public bool IsDirectory => entry.IsDirectory;
    public long Length => entry.Length;
    public DateTimeOffset ModifiedAt => entry.ModifiedAt;
    private System.Windows.Media.ImageSource? _thumbnail;
    public System.Windows.Media.ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
