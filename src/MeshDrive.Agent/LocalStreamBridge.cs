using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MeshDrive.Agent;

public sealed class LocalStreamBridge(RemoteStorageClient remote, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private static readonly string[] RequestHeaders = ["Range", "If-Range", "If-None-Match", "If-Modified-Since"];
    private static readonly string[] ResponseHeaders = ["Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified"];
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private WebApplication? _app;
    public Uri? BaseAddress { get; private set; }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "");
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapMethods("/stream/{token}/{name}", ["GET", "HEAD"], RelayAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseAddress = new Uri(app.Urls.Single());
    }
    public async Task<string> CreateAsync(string deviceId, string shareId, string path, CancellationToken cancellationToken)
    {
        var resource = RemoteStorageClient.Resource("content", shareId, path);
        using var check = await remote.SendAsync(deviceId, HttpMethod.Head, resource, null, cancellationToken).ConfigureAwait(false);
        check.EnsureSuccessStatusCode();
        foreach (var entry in _sessions.Where(e => _clock.GetUtcNow() - e.Value.LastUsed >= IdleLifetime)) _sessions.TryRemove(entry.Key, out _);
        if (_sessions.Count >= 64) throw new IOException("열린 재생 세션이 너무 많습니다. 기존 재생을 종료하세요.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new(deviceId, resource, _clock.GetUtcNow());
        return new Uri(BaseAddress ?? throw new InvalidOperationException("스트림 브리지가 준비되지 않았습니다."),
            $"/stream/{token}/{Uri.EscapeDataString(Path.GetFileName(path))}").AbsoluteUri;
    }
    public void Revoke(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) _sessions.TryRemove(uri.AbsolutePath.Split('/').ElementAtOrDefault(2) ?? "", out _);
    }
    private async Task RelayAsync(HttpContext context, string token)
    {
        if (!_sessions.TryGetValue(token, out var session) || _clock.GetUtcNow() - session.LastUsed >= IdleLifetime)
        {
            _sessions.TryRemove(token, out _); context.Response.StatusCode = 410; return;
        }
        session.LastUsed = _clock.GetUtcNow();
        try
        {
            using var response = await remote.SendAsync(session.DeviceId, new HttpMethod(context.Request.Method), session.Resource,
                request =>
                {
                    foreach (var name in RequestHeaders)
                        if (context.Request.Headers.TryGetValue(name, out var value)) request.Headers.TryAddWithoutValidation(name, value.ToArray());
                }, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in ResponseHeaders)
            {
                if (response.Content.Headers.TryGetValues(name, out var values) || response.Headers.TryGetValues(name, out values))
                    context.Response.Headers[name] = values.ToArray();
            }
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method != "HEAD")
            {
                await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) != 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted).ConfigureAwait(false);
                    session.LastUsed = _clock.GetUtcNow();
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; else context.Abort(); }
    }
    public async ValueTask DisposeAsync() { _sessions.Clear(); if (_app is not null) await _app.DisposeAsync().ConfigureAwait(false); }
    private sealed class Session(string deviceId, string resource, DateTimeOffset lastUsed)
    {
        public string DeviceId { get; } = deviceId;
        public string Resource { get; } = resource;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
