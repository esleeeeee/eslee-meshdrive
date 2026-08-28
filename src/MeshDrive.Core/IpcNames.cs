using System.Text;

namespace MeshDrive.Core;

public static class IpcNames
{
    public const string AgentFileName = "MeshDrive.Agent.exe";
    public const string WindowsFileName = "MeshDrive.Windows.exe";

    private const string PipePrefix = "eslee.meshdrive.agent.v1.";
    private const string MutexPrefix = @"Local\eslee.meshdrive.agent.v1.";

    public static string DefaultPipeName => BuildPipeName(Environment.UserName);

    public static string DefaultMutexName => BuildMutexName(Environment.UserName);

    public static string BuildPipeName(string userName) => PipePrefix + Sanitize(userName);

    public static string BuildMutexName(string userName) => MutexPrefix + Sanitize(userName);

    public static string Sanitize(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        if (userName.Length == 0)
        {
            return "user";
        }

        var builder = new StringBuilder(userName.Length);
        foreach (var character in userName)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.ToString();
    }
}
