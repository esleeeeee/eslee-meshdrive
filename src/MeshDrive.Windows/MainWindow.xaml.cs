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
            SetPending(restartIfMissing ? "Agent에 연결하는 중..." : "상태를 확인하는 중...");
            if (_client is null)
            {
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
        SessionText.Text = status.SessionId;
        ClientCountText.Text = status.ClientCount.ToString(CultureInfo.InvariantCulture);
        MessageText.Text = "창을 닫아도 Agent는 계속 실행됩니다. 다시 실행하면 같은 Agent에 재연결합니다.";
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
        SessionText.Text = "-";
        ClientCountText.Text = "-";
        MessageText.Text = message;
    }

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
}
