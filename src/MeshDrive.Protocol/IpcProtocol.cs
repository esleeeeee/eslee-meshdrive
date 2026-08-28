using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshDrive.Protocol;

public static class IpcProtocol
{
    public const int Version = 1;
    public const string ClientKindGui = "gui";
    public const string StateRunning = "running";

    public const string TypeHello = "hello";
    public const string TypeHelloAck = "hello-ack";
    public const string TypeGetStatus = "get-status";
    public const string TypeStatus = "status";
    public const string TypeShutdown = "shutdown";
    public const string TypeShutdownAck = "shutdown-ack";
    public const string TypeError = "error";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, SerializerOptions);
    }

    public static IpcMessage? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IpcMessage>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryValidateHello(IpcMessage message, out string error)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Type, TypeHello, StringComparison.Ordinal))
        {
            error = "첫 메시지는 hello여야 합니다.";
            return false;
        }

        if (message.ProtocolVersion != Version)
        {
            error = $"프로토콜 버전 {Version}만 지원합니다.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static AgentStatus ToStatus(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new AgentStatus(
            string.IsNullOrWhiteSpace(message.State) ? StateRunning : message.State,
            message.ProcessId ?? 0,
            message.StartedAt ?? DateTimeOffset.MinValue,
            message.UptimeSeconds ?? 0,
            message.ProtocolVersion ?? Version,
            message.Version ?? string.Empty,
            message.SessionId ?? string.Empty,
            message.ClientCount ?? 0);
    }
}
