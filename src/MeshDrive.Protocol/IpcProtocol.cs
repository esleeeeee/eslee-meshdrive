using System.Text.Json;
using System.Text.Json.Serialization;
using MeshDrive.Core;

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
    public const string TypeGetPeers = "get-peers";
    public const string TypePeers = "peers";
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
            message.ClientCount ?? 0,
            message.DeviceId ?? string.Empty,
            message.DeviceName ?? string.Empty,
            message.Discovery ?? string.Empty);
    }

    public static IReadOnlyList<DiscoveredPeer> ToPeers(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Peers is null || message.Peers.Count == 0)
        {
            return [];
        }

        var peers = new List<DiscoveredPeer>(message.Peers.Count);
        foreach (var payload in message.Peers)
        {
            if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
            {
                continue;
            }

            peers.Add(new DiscoveredPeer(
                payload.DeviceId,
                string.IsNullOrWhiteSpace(payload.Name) ? payload.DeviceId : payload.Name,
                payload.Ipv4 ?? string.Empty,
                payload.Port ?? DiscoveryNames.DefaultPort,
                payload.Online ?? false,
                payload.LastSeen ?? DateTimeOffset.MinValue));
        }

        return peers;
    }

    public static List<IpcPeer> ToPeerPayloads(IReadOnlyList<DiscoveredPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var payloads = new List<IpcPeer>(peers.Count);
        foreach (var peer in peers)
        {
            payloads.Add(new IpcPeer
            {
                DeviceId = peer.DeviceId,
                Name = peer.Name,
                Ipv4 = peer.Ipv4,
                Port = peer.Port,
                Online = peer.IsOnline,
                LastSeen = peer.LastSeen,
            });
        }

        return payloads;
    }
}
