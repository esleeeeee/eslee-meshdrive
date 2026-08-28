using System.IO.Pipes;

namespace MeshDrive.Protocol;

public sealed class AgentIpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private AgentIpcClient(NamedPipeClientStream stream, StreamReader reader, StreamWriter writer, AgentStatus hello)
    {
        _stream = stream;
        _reader = reader;
        _writer = writer;
        LastStatus = hello;
    }

    public AgentStatus LastStatus { get; private set; }

    public static async Task<AgentIpcClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ConnectOnceAsync(pipeName, Remaining(deadline), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                last = exception;
                var delay = TimeSpan.FromMilliseconds(100);
                var remaining = Remaining(deadline);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                if (delay > remaining)
                {
                    delay = remaining;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"Agent Named Pipe '{pipeName}'에 연결하지 못했습니다.", last);
    }

    public static async Task<AgentIpcClient> ConnectOrStartAsync(
        string pipeName,
        string agentExecutablePath,
        IReadOnlyList<string>? agentArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        try
        {
            return await ConnectAsync(pipeName, TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        AgentProcessLauncher.Start(agentExecutablePath, agentArguments);
        return await ConnectAsync(pipeName, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        RequestAsync(IpcProtocol.TypeGetStatus, IpcProtocol.TypeStatus, cancellationToken);

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _ = await RequestAsync(IpcProtocol.TypeShutdown, IpcProtocol.TypeShutdownAck, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<AgentIpcClient> ConnectOnceAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var milliseconds = timeout <= TimeSpan.Zero
                ? 1
                : (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            await stream.ConnectAsync(milliseconds, cancellationToken).ConfigureAwait(false);
            var reader = PipeUtf8.CreateReader(stream);
            var writer = PipeUtf8.CreateWriter(stream);
            try
            {
                await writer.WriteLineAsync(
                        IpcProtocol.Serialize(new IpcMessage
                        {
                            Type = IpcProtocol.TypeHello,
                            ProtocolVersion = IpcProtocol.Version,
                            ClientKind = IpcProtocol.ClientKindGui,
                            ProcessId = Environment.ProcessId,
                        }).AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException("Agent가 hello 응답 전에 연결을 닫았습니다.");
                }

                var message = IpcProtocol.TryDeserialize(line)
                    ?? throw new IOException("Agent hello 응답을 해석하지 못했습니다.");
                if (string.Equals(message.Type, IpcProtocol.TypeError, StringComparison.Ordinal))
                {
                    throw new IOException(message.Error ?? "Agent가 hello를 거부했습니다.");
                }

                if (!string.Equals(message.Type, IpcProtocol.TypeHelloAck, StringComparison.Ordinal))
                {
                    throw new IOException($"Agent hello 응답이 올바르지 않습니다: {message.Type}");
                }

                return new AgentIpcClient(stream, reader, writer, IpcProtocol.ToStatus(message));
            }
            catch
            {
                await writer.DisposeAsync().ConfigureAwait(false);
                reader.Dispose();
                throw;
            }
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AgentStatus> RequestAsync(
        string requestType,
        string expectedResponseType,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            await _writer.WriteLineAsync(
                    IpcProtocol.Serialize(new IpcMessage
                    {
                        Type = requestType,
                        ProtocolVersion = IpcProtocol.Version,
                        Id = id,
                    }).AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);

            while (true)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException("Agent와의 연결이 끊어졌습니다.");
                }

                var message = IpcProtocol.TryDeserialize(line)
                    ?? throw new IOException("Agent 응답을 해석하지 못했습니다.");
                if (message.Id is int responseId && responseId != id)
                {
                    continue;
                }

                if (string.Equals(message.Type, IpcProtocol.TypeError, StringComparison.Ordinal))
                {
                    throw new IOException(message.Error ?? "Agent가 요청을 거부했습니다.");
                }

                if (!string.Equals(message.Type, expectedResponseType, StringComparison.Ordinal))
                {
                    throw new IOException($"Agent 응답이 올바르지 않습니다: {message.Type}");
                }

                if (string.Equals(expectedResponseType, IpcProtocol.TypeStatus, StringComparison.Ordinal) ||
                    string.Equals(expectedResponseType, IpcProtocol.TypeHelloAck, StringComparison.Ordinal))
                {
                    LastStatus = IpcProtocol.ToStatus(message);
                    return LastStatus;
                }

                return LastStatus;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan Remaining(DateTime utcDeadline)
    {
        var remaining = utcDeadline - DateTime.UtcNow;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
