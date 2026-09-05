namespace MeshDrive.Core;

public enum SyncMode { TwoWay, Push, Pull }
public enum SyncAction { None, CopyLeftToRight, CopyRightToLeft, DeleteLeft, DeleteRight, Conflict }
public sealed record SyncBaseline(string? LeftHash, string? RightHash);

public static class SyncPlanner
{
    public static SyncAction Decide(string? left, string? right, SyncBaseline? baseline, SyncMode mode)
    {
        if (left == right) return SyncAction.None;
        var leftChanged = left != baseline?.LeftHash;
        var rightChanged = right != baseline?.RightHash;
        if (mode == SyncMode.Push)
        {
            if (rightChanged && right is not null) return SyncAction.Conflict;
            return left is null ? SyncAction.DeleteRight : SyncAction.CopyLeftToRight;
        }
        if (mode == SyncMode.Pull)
        {
            if (leftChanged && left is not null) return SyncAction.Conflict;
            return right is null ? SyncAction.DeleteLeft : SyncAction.CopyRightToLeft;
        }
        if (leftChanged && rightChanged) return SyncAction.Conflict;
        if (leftChanged) return left is null ? SyncAction.DeleteRight : SyncAction.CopyLeftToRight;
        if (rightChanged) return right is null ? SyncAction.DeleteLeft : SyncAction.CopyRightToLeft;
        return SyncAction.None;
    }

    public static string ConflictPath(string relativePath, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new ArgumentException("올바르지 않은 파일 해시입니다.", nameof(hash));
        var slash = relativePath.LastIndexOf('/');
        var prefix = slash < 0 ? "" : relativePath[..(slash + 1)];
        var name = slash < 0 ? relativePath : relativePath[(slash + 1)..];
        return prefix + Path.GetFileNameWithoutExtension(name) + ".conflict-" + hash[..12].ToLowerInvariant() + Path.GetExtension(name);
    }
}
