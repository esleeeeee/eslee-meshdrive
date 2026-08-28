using System.IO.Pipes;
using System.Text;
using MeshDrive.Protocol;

namespace MeshDrive.Tests;

internal sealed class RawIpcConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public RawIpcConnection(string pipeName)
    {
        _stream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task ConnectAndHelloAsync(CancellationToken cancellationToken)
    {
        await _stream.ConnectAsync(5000, cancellationToken);
        _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };
        await _writer.WriteLineAsync(
            IpcProtocol.Serialize(new IpcMessage
            {
                Type = IpcProtocol.TypeHello,
                ProtocolVersion = IpcProtocol.Version,
                ClientKind = IpcProtocol.ClientKindGui,
                ProcessId = Environment.ProcessId,
            }).AsMemory(),
            cancellationToken);
        var line = await _reader.ReadLineAsync(cancellationToken);
        Assert.IsNotNull(line);
        var ack = IpcProtocol.TryDeserialize(line);
        Assert.IsNotNull(ack);
        Assert.AreEqual(IpcProtocol.TypeHelloAck, ack.Type);
    }

    public async Task<IpcMessage> SendAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        Assert.IsNotNull(_writer);
        Assert.IsNotNull(_reader);
        await _writer.WriteLineAsync(IpcProtocol.Serialize(message).AsMemory(), cancellationToken);
        var line = await _reader.ReadLineAsync(cancellationToken);
        Assert.IsNotNull(line);
        var response = IpcProtocol.TryDeserialize(line);
        Assert.IsNotNull(response);
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
        }

        _reader?.Dispose();
        await _stream.DisposeAsync();
    }
}
