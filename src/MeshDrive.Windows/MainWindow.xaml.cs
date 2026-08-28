using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Windows;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x65, 0x7A));
    private static readonly Brush PendingBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x73, 0x80));

    private readonly DispatcherTimer _refreshTimer;
    private AgentIpcClient? _client;
    private int _busy;
    private bool _fullExit;

    public MainWindow()
    {
        InitializeComponent();
        ConnectedBrush.Freeze();
        DisconnectedBrush.Freeze();
        PendingBrush.Freeze();
        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += RefreshTimerOnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ConnectAsync(restartIfMissing: true);
        _refreshTimer.Start();
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e) =>
        await RefreshAsync(restartIfMissing: false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ConnectAsync(restartIfMissing: true);

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async void ExitAll_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        ExitAllButton.IsEnabled = false;
        try
        {
            if (_client is not null)
            {
                try
                {
                    await _client.ShutdownAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }

            _fullExit = true;
            await DisposeClientAsync();
            Close();
        }
        catch (Exception exception)
        {
            SetDisconnected("종료 요청에 실패했습니다. " + exception.Message);
            ExitAllButton.IsEnabled = true;
        }
        finally
        {
            if (!_fullExit)
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        if (!_fullExit)
        {
            await DisposeClientAsync();
        }
    }

    private async Task ConnectAsync(bool restartIfMissing)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            if (_client is null)
            {
                SetPending(restartIfMissing ? "Agent에 연결하는 중..." : "상태를 확인하는 중...");
                if (restartIfMissing)
                {
                    _client = await AgentIpcClient.ConnectOrStartAsync(
                        IpcNames.DefaultPipeName,
                        AgentProcessLauncher.ResolveExecutablePath(),
                        agentArguments: null,
                        ConnectTimeout,
                        CancellationToken.None);
                }
                else
                {
                    _client = await AgentIpcClient.ConnectAsync(
                        IpcNames.DefaultPipeName,
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None);
                }
            }

            var status = await _client.GetStatusAsync(CancellationToken.None);
            ShowStatus(status);
            var peers = await _client.GetPeersAsync(CancellationToken.None);
            ShowPeers(peers);
            var pairing = await _client.GetPairingAsync(CancellationToken.None);
            ShowPairing(pairing);
            var trusted = await _client.GetTrustedAsync(CancellationToken.None);
            ShowTrusted(trusted);
        }
        catch (Exception exception)
        {
            await DisposeClientAsync();
            SetDisconnected(restartIfMissing
                ? "Agent에 연결하지 못했습니다. " + exception.Message
                : "Agent 연결이 끊어졌습니다. 새로고침을 누르면 다시 연결합니다.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task RefreshAsync(bool restartIfMissing)
    {
        if (_client is null)
        {
            return;
        }

        await ConnectAsync(restartIfMissing);
    }

    private void ShowStatus(AgentStatus status)
    {
        StatusDot.Fill = ConnectedBrush;
        ConnectionText.Text = "Agent 연결됨";
        ProcessIdText.Text = status.ProcessId.ToString(CultureInfo.InvariantCulture);
        StartedAtText.Text = status.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        UptimeText.Text = FormatUptime(status.UptimeSeconds);
        ProtocolText.Text = status.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        VersionText.Text = status.Version;
        DeviceNameText.Text = string.IsNullOrWhiteSpace(status.DeviceName) ? "-" : status.DeviceName;
        DiscoveryText.Text = FormatDiscovery(status.Discovery);
        MessageText.Text = "창을 닫아도 Agent는 계속 실행됩니다. 다시 실행하면 같은 Agent에 재연결합니다.";
    }

    private void ShowPeers(IReadOnlyList<DiscoveredPeer> peers)
    {
        var selectedId = (PeerList.SelectedItem as PeerRow)?.DeviceId;
        var rows = peers.Select(static peer => new PeerRow(
            peer.DeviceId,
            peer.Name,
            peer.IsOnline ? "온라인" : "오프라인",
            FormatTrust(peer.TrustState),
            string.IsNullOrWhiteSpace(peer.Ipv4) ? "-" : peer.Ipv4,
            peer.IsOnline && peer.TrustState != TrustStates.Trusted)).ToArray();
        PeerList.ItemsSource = rows;
        EmptyPeersText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedId is not null)
        {
            PeerList.SelectedItem = rows.FirstOrDefault(row => row.DeviceId == selectedId);
        }

        UpdatePairButton();
    }

    private void ShowPairing(IpcMessage pairing)
    {
        var waiting = string.Equals(pairing.PairingStatus, "waiting", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pairing.Sas);
        PairingBanner.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        if (!waiting)
        {
            return;
        }

        PairingPeerText.Text = string.IsNullOrWhiteSpace(pairing.DeviceName)
            ? pairing.DeviceId
            : pairing.DeviceName + "와 페어링";
        PairingSasText.Text = SasCalculator.FormatDisplay(pairing.Sas ?? string.Empty);
    }

    private void ShowTrusted(IpcMessage trusted)
    {
        var selectedId = (TrustedList.SelectedItem as TrustedRow)?.DeviceId;
        var rows = (trusted.Trusted ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item.DeviceId))
            .Select(static item => new TrustedRow(item.DeviceId!, item.Name ?? item.DeviceId!))
            .ToArray();
        TrustedList.ItemsSource = rows;
        if (selectedId is not null)
        {
            TrustedList.SelectedItem = rows.FirstOrDefault(row => row.DeviceId == selectedId);
        }

        UpdateUnpairButton();
    }

    private void SetPending(string message)
    {
        StatusDot.Fill = PendingBrush;
        ConnectionText.Text = "연결 중";
        MessageText.Text = message;
    }

    private void SetDisconnected(string message)
    {
        StatusDot.Fill = DisconnectedBrush;
        ConnectionText.Text = "연결 끊김";
        ProcessIdText.Text = "-";
        StartedAtText.Text = "-";
        UptimeText.Text = "-";
        ProtocolText.Text = "-";
        VersionText.Text = "-";
        DeviceNameText.Text = "-";
        DiscoveryText.Text = "-";
        PeerList.ItemsSource = Array.Empty<PeerRow>();
        EmptyPeersText.Visibility = Visibility.Visible;
        TrustedList.ItemsSource = Array.Empty<TrustedRow>();
        PairingBanner.Visibility = Visibility.Collapsed;
        PairButton.IsEnabled = false;
        UnpairButton.IsEnabled = false;
        MessageText.Text = message;
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || PeerList.SelectedItem is not PeerRow peer)
        {
            return;
        }

        try
        {
            var pairing = await _client.StartPairingAsync(peer.DeviceId, ipv4: null, port: null, CancellationToken.None);
            ShowPairing(pairing);
            MessageText.Text = "상대 기기에도 같은 인증번호가 보이는지 확인하세요.";
        }
        catch (Exception exception)
        {
            MessageText.Text = "페어링을 시작하지 못했습니다. " + exception.Message;
        }
    }

    private async void ApprovePairing_Click(object sender, RoutedEventArgs e) =>
        await DecidePairingAsync(accepted: true);

    private async void RejectPairing_Click(object sender, RoutedEventArgs e) =>
        await DecidePairingAsync(accepted: false);

    private async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || TrustedList.SelectedItem is not TrustedRow trusted)
        {
            return;
        }

        try
        {
            await _client.UnpairAsync(trusted.DeviceId, CancellationToken.None);
            var remaining = await _client.GetTrustedAsync(CancellationToken.None);
            ShowTrusted(remaining);
            var peers = await _client.GetPeersAsync(CancellationToken.None);
            ShowPeers(peers);
            MessageText.Text = "연결을 해제했습니다. 다시 쓰려면 페어링해야 합니다.";
        }
        catch (Exception exception)
        {
            MessageText.Text = "연결 해제에 실패했습니다. " + exception.Message;
        }
    }

    private void PeerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdatePairButton();

    private void TrustedList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateUnpairButton();

    private async Task DecidePairingAsync(bool accepted)
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            var pairing = await _client.DecidePairingAsync(accepted, CancellationToken.None);
            ShowPairing(pairing);
            var trusted = await _client.GetTrustedAsync(CancellationToken.None);
            ShowTrusted(trusted);
            var peers = await _client.GetPeersAsync(CancellationToken.None);
            ShowPeers(peers);
            MessageText.Text = accepted
                ? (string.Equals(pairing.PairingStatus, "completed", StringComparison.OrdinalIgnoreCase)
                    ? "페어링이 완료되었습니다."
                    : "승인을 보냈습니다. 상대 기기의 승인을 기다립니다.")
                : "페어링을 거절했습니다.";
        }
        catch (Exception exception)
        {
            MessageText.Text = "페어링 결정에 실패했습니다. " + exception.Message;
        }
    }

    private void UpdatePairButton() =>
        PairButton.IsEnabled = _client is not null && PeerList.SelectedItem is PeerRow peer && peer.CanPair;

    private void UpdateUnpairButton() =>
        UnpairButton.IsEnabled = _client is not null && TrustedList.SelectedItem is TrustedRow;

    private async Task DisposeClientAsync()
    {
        if (_client is null)
        {
            return;
        }

        var client = _client;
        _client = null;
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private static string FormatUptime(long seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var value = TimeSpan.FromSeconds(seconds);
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}시간 {value.Minutes}분 {value.Seconds}초";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{value.Minutes}분 {value.Seconds}초";
        }

        return $"{value.Seconds}초";
    }

    private static string FormatDiscovery(string discovery) =>
        string.Equals(discovery, DiscoveryNames.DiscoveryMdns, StringComparison.Ordinal)
            ? "mDNS"
            : "꺼짐";

    private static string FormatTrust(string trustState) => trustState switch
    {
        TrustStates.Trusted => "신뢰됨",
        TrustStates.Pending => "페어링 중",
        _ => "미페어링",
    };
}

public sealed record PeerRow(string DeviceId, string Name, string Status, string Trust, string Ipv4, bool CanPair);

public sealed record TrustedRow(string DeviceId, string Name);
