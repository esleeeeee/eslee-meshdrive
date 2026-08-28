using System.Diagnostics;
using MeshDrive.Core;

namespace MeshDrive.Protocol;

public static class AgentProcessLauncher
{
    public static string ResolveExecutablePath(string? baseDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        return Path.Combine(directory, IpcNames.AgentFileName);
    }

    public static Process Start(string executablePath, IReadOnlyList<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("MeshDrive Agent 실행 파일을 찾지 못했습니다.", executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        };
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("MeshDrive Agent 프로세스를 시작하지 못했습니다.");
    }
}
