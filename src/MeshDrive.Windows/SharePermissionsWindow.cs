using System.Windows;
using System.Windows.Controls;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Windows;

public sealed class SharePermissionsWindow : Window
{
    public Dictionary<string, SharePermissions> Overrides { get; }
    public SharePermissionsWindow(SharedFolder share, IEnumerable<IpcTrustedPeer> peers)
    {
        Overrides = share.DeviceOverrides is null ? new(StringComparer.Ordinal) : new(share.DeviceOverrides, StringComparer.Ordinal);
        Title = share.Name + " · 기기별 권한"; Width = 480; Height = 290; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "기기별로 기본 공유 권한을 바꿀 수 있습니다. 적용 후 저장하세요.", TextWrapping = TextWrapping.Wrap });
        var devices = new ComboBox { ItemsSource = peers.ToArray(), DisplayMemberPath = "Name", Margin = new(0, 16, 0, 12) }; panel.Children.Add(devices);
        var permissions = new ComboBox { ItemsSource = new[] { "폴더 기본값 사용", "접근 불가", "읽기 전용", "양방향 복사", "스트리밍 전용" }, SelectedIndex = 0 }; panel.Children.Add(permissions);
        var apply = new Button { Content = "선택 기기에 적용", Margin = new(0, 12, 0, 12) }; panel.Children.Add(apply);
        var save = new Button { Content = "저장", IsDefault = true }; panel.Children.Add(save);
        devices.SelectionChanged += (_, _) =>
        {
            permissions.SelectedIndex = devices.SelectedItem is IpcTrustedPeer { DeviceId: { } id } && Overrides.TryGetValue(id, out var value)
                ? value switch { SharePermissions.None => 1, SharePermissions.ReadOnly => 2, SharePermissions.All => 3, _ => 4 } : 0;
        };
        void Apply()
        {
            if (devices.SelectedItem is not IpcTrustedPeer { DeviceId: { } id }) return;
            if (permissions.SelectedIndex == 0) Overrides.Remove(id);
            else Overrides[id] = permissions.SelectedIndex switch { 1 => SharePermissions.None, 2 => SharePermissions.ReadOnly, 3 => SharePermissions.All, _ => SharePermissions.Browse | SharePermissions.Stream };
        }
        apply.Click += (_, _) => Apply();
        save.Click += (_, _) => { Apply(); DialogResult = true; };
    }
}
