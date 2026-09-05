using System.Text.Json;
using MeshDrive.Core;
using Microsoft.Win32;

namespace MeshDrive.Agent;

public sealed class AgentSettings
{
    public string? DeviceName { get; set; }
    public bool SharingPaused { get; set; }
    public bool OnboardingComplete { get; set; }
    public static AgentSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path)) ?? new() : new();
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this)); File.Move(path + ".tmp", path, true);
    }
    public static bool AutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        return key?.GetValue("MeshDrive") is string;
    }
    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        if (enabled) key.SetValue("MeshDrive", '"' + Path.Combine(AppContext.BaseDirectory, "MeshDrive.Agent.exe") + '"');
        else key.DeleteValue("MeshDrive", false);
    }
}
