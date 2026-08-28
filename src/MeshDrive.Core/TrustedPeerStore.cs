using System.Text.Json;

namespace MeshDrive.Core;

public sealed record TrustedPeer(
    string DeviceId,
    string Name,
    string Fingerprint,
    DateTimeOffset PairedAt);

public sealed class TrustedPeerStore
{
    public const string FileName = "trusted-peers.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrustedPeer> _peers = new(StringComparer.Ordinal);

    public TrustedPeerStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
        Load();
    }

    public IReadOnlyList<TrustedPeer> Snapshot()
    {
        lock (_gate)
        {
            return _peers.Values
                .OrderBy(static peer => peer.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static peer => peer.DeviceId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool IsTrusted(string deviceId, string fingerprint)
    {
        lock (_gate)
        {
            return _peers.TryGetValue(deviceId, out var peer) &&
                DeviceFingerprints.FixedEquals(peer.Fingerprint, fingerprint);
        }
    }

    public bool IsTrustedFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (_gate)
        {
            foreach (var peer in _peers.Values)
            {
                if (DeviceFingerprints.FixedEquals(peer.Fingerprint, fingerprint))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryGetFingerprint(string deviceId, out string fingerprint)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(deviceId, out var peer))
            {
                fingerprint = peer.Fingerprint;
                return true;
            }
        }

        fingerprint = string.Empty;
        return false;
    }

    public void Trust(string deviceId, string name, string fingerprint, DateTimeOffset pairedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (_gate)
        {
            _peers[deviceId] = new TrustedPeer(
                deviceId,
                string.IsNullOrWhiteSpace(name) ? deviceId : name,
                fingerprint,
                pairedAt);
            Save();
        }
    }

    public bool Unpair(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_gate)
        {
            if (!_peers.Remove(deviceId))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<TrustedFile>(json, JsonOptions);
            if (file?.Peers is null)
            {
                return;
            }

            foreach (var peer in file.Peers)
            {
                if (peer is null ||
                    !DeviceIdentityStore.IsUsableDeviceId(peer.DeviceId) ||
                    string.IsNullOrWhiteSpace(peer.Fingerprint))
                {
                    continue;
                }

                _peers[peer.DeviceId] = peer;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void Save()
    {
        var payload = JsonSerializer.Serialize(new TrustedFile { Peers = Snapshot().ToList() }, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, payload);
        File.Copy(temp, _path, overwrite: true);
        File.Delete(temp);
    }

    private sealed class TrustedFile
    {
        public List<TrustedPeer>? Peers { get; set; }
    }
}
