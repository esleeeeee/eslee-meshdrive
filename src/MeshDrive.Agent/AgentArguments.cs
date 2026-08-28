using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed record AgentHostOptions(string PipeName, string MutexName);

public static class AgentArguments
{
    public static bool TryParse(IReadOnlyList<string> args, out AgentHostOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? pipeName = null;
        string? mutexName = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--pipe-name")
            {
                if (!TryReadValue(args, ref index, "--pipe-name", out pipeName, out error))
                {
                    options = null!;
                    return false;
                }

                continue;
            }

            if (argument is "--mutex-name")
            {
                if (!TryReadValue(args, ref index, "--mutex-name", out mutexName, out error))
                {
                    options = null!;
                    return false;
                }

                continue;
            }

            options = null!;
            error = $"알 수 없는 인수입니다: {argument}";
            return false;
        }

        options = new AgentHostOptions(
            string.IsNullOrWhiteSpace(pipeName) ? IpcNames.DefaultPipeName : pipeName,
            string.IsNullOrWhiteSpace(mutexName) ? IpcNames.DefaultMutexName : mutexName);
        error = string.Empty;
        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string name,
        out string value,
        out string error)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            value = string.Empty;
            error = $"{name} 값이 필요합니다.";
            return false;
        }

        value = args[++index];
        error = string.Empty;
        return true;
    }
}
