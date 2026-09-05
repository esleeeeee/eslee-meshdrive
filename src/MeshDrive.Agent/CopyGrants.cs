using System.Security.Cryptography;
using MeshDrive.Core;
using MeshDrive.Protocol;

namespace MeshDrive.Agent;

public sealed class CopyGrants(StorageService storage, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (CopyGrant Grant, DateTimeOffset Used)> _grants = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public CopyTicket Create(string requester, CopyGrantRequest request)
    {
        storage.Resolve(requester, request.ShareId, request.Path, SharePermissions.Download);
        storage.Resolve(request.TargetDeviceId, request.ShareId, request.Path, SharePermissions.Download);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var token in _grants.Where(g => now - g.Value.Used >= IdleLifetime).Select(g => g.Key).ToArray()) _grants.Remove(token);
            if (_grants.Count >= 256) throw new IOException("직접 복사 요청이 너무 많습니다.");
            var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _grants[value] = (new(requester, request.TargetDeviceId, request.ShareId, request.Path), now);
            return new(value);
        }
    }

    public CopyGrant Validate(string token, string target, string? share = null, string? path = null)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!_grants.TryGetValue(token, out var item) || now - item.Used >= IdleLifetime || item.Grant.TargetDeviceId != target ||
                (share is not null && (item.Grant.ShareId != share || item.Grant.Path != path)))
                throw new UnauthorizedAccessException("직접 복사 권한이 없거나 만료되었습니다.");
            storage.Resolve(item.Grant.RequesterId, item.Grant.ShareId, item.Grant.Path, SharePermissions.Download);
            storage.Resolve(target, item.Grant.ShareId, item.Grant.Path, SharePermissions.Download);
            _grants[token] = (item.Grant, now);
            return item.Grant;
        }
    }
}
