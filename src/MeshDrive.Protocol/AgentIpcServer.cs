using System.Collections.Concurrent;
using System.IO.Pipes;
using MeshDrive.Core;

namespace MeshDrive.Protocol;

public sealed class AgentIpcServer : IAsyncDisposable
{
    private const int MaxClients = 8;
    private static readonly TimeSpan FaultRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _pipeName;
    private readonly DateTimeOffset _startedAt;
    private readonly int _processId;
    private readonly string _version;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _discovery;
    private readonly Func<string>? _discoveryProvider;
    private readonly Func<IReadOnlyList<DiscoveredPeer>>? _listPeers;
    private readonly Func<IpcMessage, CancellationToken, Task<IpcMessage?>>? _handleCommand;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private int _clientCount;
    private int _shutdownRequested;
    private bool _disposed;

    public AgentIpcServer(
        string pipeName,
        DateTimeOffset startedAt,
        int? processId = null,
        string? version = null,
        string? deviceId = null,
        string? deviceName = null,
        string? discovery = null,
        Func<IReadOnlyList<DiscoveredPeer>>? listPeers = null,
        Func<IpcMessage, CancellationToken, Task<IpcMessage?>>? handleCommand = null,
        Func<string>? discoveryProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _startedAt = startedAt;
        _processId = processId ?? Environment.ProcessId;
        _version = string.IsNullOrWhiteSpace(version) ? AppInfo.Version : version;
        _deviceId = deviceId ?? string.Empty;
        _deviceName = deviceName ?? string.Empty;
        _discovery = string.IsNullOrWhiteSpace(discovery) ? DiscoveryNames.DiscoveryOff : discovery;
        _listPeers = listPeers;
        _handleCommand = handleCommand;
        _discoveryProvider = discoveryProvider;
    }

    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public int ConnectedClientCount => Math.Max(Volatile.Read(ref _clientCount), 0);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var accept = Task.Run(() => AcceptLoopAsync(linked.Token), CancellationToken.None);
        try
        {
            await _stopped.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await DrainAsync(accept).ConfigureAwait(false);
            await DrainSessionsAsync().ConfigureAwait(false);
        }
    }

    public void RequestShutdown() => CompleteShutdown();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CompleteShutdown();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !IsShutdownRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipe();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (IsShutdownRequested)
                {
                    break;
                }

                var connected = server;
                server = null;
                Task? sessionTask = null;
                sessionTask = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await using (connected.ConfigureAwait(false))
                            {
                                await ServeClientAsync(connected, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception exception) when (
                            exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
                        {
                        }
                        finally
                        {
                            if (sessionTask is not null)
                            {
                                _sessions.TryRemove(sessionTask, out _);
                            }
                        }
                    },
                    CancellationToken.None);
                _sessions[sessionTask] = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (IsShutdownRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(FaultRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var reader = PipeUtf8.CreateReader(server);
        var writer = PipeUtf8.CreateWriter(server);
        await using (writer.ConfigureAwait(false))
        {
            var sessionId = Guid.NewGuid().ToString("N");
            Interlocked.Increment(ref _clientCount);
            try
            {
                var helloLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (helloLine is null)
                {
                    return;
                }

                var hello = IpcProtocol.TryDeserialize(helloLine);
                string? helloError = null;
                if (hello is null)
                {
                    helloError = "hello 메시지를 해석하지 못했습니다.";
                }
                else if (!IpcProtocol.TryValidateHello(hello, out helloError))
                {
                }

                if (helloError is not null && helloError.Length > 0)
                {
                    await WriteAsync(
                            writer,
                            new IpcMessage
                            {
                                Type = IpcProtocol.TypeError,
                                ProtocolVersion = IpcProtocol.Version,
                                Error = helloError,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteAsync(writer, CreateStatusMessage(IpcProtocol.TypeHelloAck, id: null, sessionId), cancellationToken)
                    .ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested && !IsShutdownRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }

                    var message = IpcProtocol.TryDeserialize(line);
                    if (message is null)
                    {
                        await WriteAsync(
                                writer,
                                new IpcMessage
                                {
                                    Type = IpcProtocol.TypeError,
                                    ProtocolVersion = IpcProtocol.Version,
                                    Error = "메시지를 해석하지 못했습니다.",
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (string.Equals(message.Type, IpcProtocol.TypeGetStatus, StringComparison.Ordinal))
                    {
                        await WriteAsync(
                                writer,
                                CreateStatusMessage(IpcProtocol.TypeStatus, message.Id, sessionId),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (string.Equals(message.Type, IpcProtocol.TypeGetPeers, StringComparison.Ordinal))
                    {
                        await WriteAsync(writer, CreatePeersMessage(message.Id), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (_handleCommand is not null)
                    {
                        var handled = await _handleCommand(message, cancellationToken).ConfigureAwait(false);
                        if (handled is not null)
                        {
                            handled.Id = message.Id;
                            handled.ProtocolVersion = IpcProtocol.Version;
                            await WriteAsync(writer, handled, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    if (string.Equals(message.Type, IpcProtocol.TypeShutdown, StringComparison.Ordinal))
                    {
                        await WriteAsync(
                                writer,
                                new IpcMessage
                                {
                                    Type = IpcProtocol.TypeShutdownAck,
                                    ProtocolVersion = IpcProtocol.Version,
                                    Id = message.Id,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        CompleteShutdown();
                        return;
                    }

                    await WriteAsync(
                            writer,
                            new IpcMessage
                            {
                                Type = IpcProtocol.TypeError,
                                ProtocolVersion = IpcProtocol.Version,
                                Id = message.Id,
                                Error = $"알 수 없는 메시지 형식입니다: {message.Type}",
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _clientCount);
            }
        }
    }

    private IpcMessage CreateStatusMessage(string type, int? id, string sessionId)
    {
        var now = DateTimeOffset.Now;
        var uptime = now - _startedAt;
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }

        return new IpcMessage
        {
            Type = type,
            ProtocolVersion = IpcProtocol.Version,
            Id = id,
            ProcessId = _processId,
            StartedAt = _startedAt,
            UptimeSeconds = (long)uptime.TotalSeconds,
            State = IpcProtocol.StateRunning,
            Version = _version,
            SessionId = sessionId,
            ClientCount = Math.Max(Volatile.Read(ref _clientCount), 0),
            DeviceId = string.IsNullOrWhiteSpace(_deviceId) ? null : _deviceId,
            DeviceName = string.IsNullOrWhiteSpace(_deviceName) ? null : _deviceName,
            Discovery = _discoveryProvider?.Invoke() ?? _discovery,
        };
    }

    private IpcMessage CreatePeersMessage(int? id) =>
        new()
        {
            Type = IpcProtocol.TypePeers,
            ProtocolVersion = IpcProtocol.Version,
            Id = id,
            Peers = IpcProtocol.ToPeerPayloads(_listPeers?.Invoke() ?? []),
        };

    private NamedPipeServerStream CreatePipe() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);

    private static async Task WriteAsync(StreamWriter writer, IpcMessage message, CancellationToken cancellationToken) =>
        await writer.WriteLineAsync(IpcProtocol.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);

    private void CompleteShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        _stopped.TrySetResult();
        _lifetime.Cancel();
    }

    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task DrainSessionsAsync()
    {
        var sessions = _sessions.Keys.ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (AggregateException)
        {
        }
    }
}
