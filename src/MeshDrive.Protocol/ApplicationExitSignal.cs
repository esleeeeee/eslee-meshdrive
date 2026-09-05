using MeshDrive.Core;

namespace MeshDrive.Protocol;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ApplicationExitSignal
{
    public static EventWaitHandle Create() => new(false, EventResetMode.ManualReset, "Local\\" + IpcNames.DefaultPipeName + ".shutdown");
    public static bool IsRequested()
    {
        try { using var signal = EventWaitHandle.OpenExisting("Local\\" + IpcNames.DefaultPipeName + ".shutdown"); return signal.WaitOne(0); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
}
