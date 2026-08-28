using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MeshDrive.Core;

public sealed record DeviceIdentity(string DeviceId, string DeviceName);

public static class DeviceIdentityStore
{
    public const string FileName = "device-identity.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static DeviceIdentity LoadOrCreate(string dataDirectory, string? machineName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var deviceName = NormalizeName(machineName ?? Environment.MachineName);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var stored = JsonSerializer.Deserialize<IdentityFile>(json, JsonOptions);
                if (stored is not null && IsUsableDeviceId(stored.DeviceId))
                {
                    return new DeviceIdentity(stored.DeviceId, deviceName);
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }

        var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), deviceName);
        var payload = JsonSerializer.Serialize(new IdentityFile { DeviceId = identity.DeviceId }, JsonOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, payload);
        File.Copy(temp, path, overwrite: true);
        File.Delete(temp);
        return identity;
    }

    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "MeshDrive-PC" : trimmed;
    }

    public static bool IsUsableDeviceId([NotNullWhen(true)] string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 63)
        {
            return false;
        }

        foreach (var character in deviceId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-')
            {
                return false;
            }
        }

        return true;
    }

    private sealed class IdentityFile
    {
        public string? DeviceId { get; set; }
    }
}
