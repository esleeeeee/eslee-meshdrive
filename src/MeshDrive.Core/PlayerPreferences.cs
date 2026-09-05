using System.Text.Json;

namespace MeshDrive.Core;

public sealed class PlayerPreferences
{
    public string? MusicPlayer { get; set; }
    public string? VideoPlayer { get; set; }
    public static bool IsMusic(string path) => new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static PlayerPreferences Load(string directory)
    {
        var path = Path.Combine(directory, "players.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<PlayerPreferences>(File.ReadAllText(path)) ?? new() : new();
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "players.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this)); File.Move(path + ".tmp", path, true);
    }
}
