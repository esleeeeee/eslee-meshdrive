namespace MeshDrive.Core;

public static class AppPaths
{
    public static string DefaultDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eslee",
            "MeshDrive");
}
