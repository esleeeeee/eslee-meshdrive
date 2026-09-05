using System.Windows;
using System.Windows.Controls;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Windows;

public sealed class CopyTargetWindow : Window
{
    private readonly ComboBox _devices = new() { DisplayMemberPath = "Name", Margin = new(0, 8, 0, 8) };
    private readonly ComboBox _shares = new() { DisplayMemberPath = "Name", Margin = new(0, 8, 0, 8) };
    private readonly ListBox _folders = new() { DisplayMemberPath = "Name", Height = 230 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Func<StorageCommand, Task<StorageReply>> _send;
    private string _path = "";
    private int _generation;
    public IpcTrustedPeer? TargetDevice => _devices.SelectedItem as IpcTrustedPeer;
    public RemoteShare? TargetShare => _shares.SelectedItem as RemoteShare;
    public string TargetPath => _path;

    public CopyTargetWindow(IEnumerable<IpcTrustedPeer> devices, Func<StorageCommand, Task<StorageReply>> send)
    {
        _send = send;
        Title = "다른 기기로 직접 복사"; Width = 520; Height = 500; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "받을 기기와 폴더를 선택하세요. 세 기기가 서로 페어링되어 있어야 합니다.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_devices); panel.Children.Add(_shares);
        var up = new Button { Content = "상위 폴더" }; panel.Children.Add(up); panel.Children.Add(_folders); panel.Children.Add(_status);
        var copy = new Button { Content = "현재 폴더로 복사", IsDefault = true }; panel.Children.Add(copy);
        _devices.ItemsSource = devices.ToArray();
        _devices.SelectionChanged += async (_, _) => await RunAsync(async () =>
        {
            var generation = ++_generation; _shares.ItemsSource = null; _folders.ItemsSource = null; _path = "";
            if (TargetDevice is not { } device) return;
            var shares = (await _send(new() { Action = "remote-shares", DeviceId = device.DeviceId })).Shares;
            if (generation == _generation) _shares.ItemsSource = shares?.Where(s => s.Permissions.HasFlag(SharePermissions.Upload)).ToArray();
        });
        _shares.SelectionChanged += async (_, _) => { _path = ""; await RunAsync(LoadAsync); };
        _folders.MouseDoubleClick += async (_, _) => { if (_folders.SelectedItem is RemoteEntry folder) { _path = folder.RelativePath; await RunAsync(LoadAsync); } };
        up.Click += async (_, _) => { var index = _path.LastIndexOf('/'); _path = index < 0 ? "" : _path[..index]; await RunAsync(LoadAsync); };
        copy.Click += (_, _) => { if (TargetDevice is not null && TargetShare is not null) DialogResult = true; };
    }
    private async Task LoadAsync()
    {
        var generation = ++_generation;
        if (TargetDevice is not { } device || TargetShare is not { } share) return;
        _status.Text = $"{share.Name} / {_path}";
        var entries = (await _send(new() { Action = "entries", DeviceId = device.DeviceId, ShareId = share.Id, Path = _path })).Entries;
        if (generation == _generation) _folders.ItemsSource = entries?.Where(e => e.IsDirectory).ToArray();
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { _status.Text = e.Message; }
    }
}
