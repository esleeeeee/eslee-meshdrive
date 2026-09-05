using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MeshDrive.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace MeshDrive.Agent;

public sealed class AgentHttpsHost : IAsyncDisposable
{
    private readonly DeviceCredential _credential;
    private readonly PairingCoordinator _coordinator;
    private readonly DeviceIdentity _identity;
    private readonly int _port;
    private WebApplication? _app;
    public StorageService? Storage { get; init; }
    public PhotoCache? Thumbnails { get; init; }

    public AgentHttpsHost(
        DeviceIdentity identity,
        DeviceCredential credential,
        PairingCoordinator coordinator,
        int port)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        _identity = identity;
        _credential = credential;
        _coordinator = coordinator;
        _port = port;
    }

    public int Port => _port;

    public bool IsRunning => _app is not null;

    public async Task<bool> TryStartAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return true;
        }

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = "MeshDrive.Agent",
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseSetting("urls", string.Empty);
            builder.WebHost.UseKestrel(options =>
            {
                options.Listen(IPAddress.Any, _port, listen =>
                {
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = _credential.Certificate;
                        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        https.AllowAnyClientCertificate();
                        https.CheckCertificateRevocation = false;
                        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    });
                });
            });

            var app = builder.Build();
            app.Use(ValidateClientCertificateAsync);
            app.Use(async (context, next) =>
            {
                try { await next(context).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    if (context.Response.HasStarted) throw;
                    context.Response.StatusCode = e is UnauthorizedAccessException ? 403 : e is FileNotFoundException or DirectoryNotFoundException ? 404 : 400;
                    await context.Response.WriteAsync("파일에 접근할 수 없습니다. 공유 권한과 파일 상태를 확인하세요.", context.RequestAborted).ConfigureAwait(false);
                }
            });
            app.MapPost("/v1/pairing/offer", HandleOfferAsync);
            app.MapPost("/v1/pairing/decision", HandleDecisionAsync);
            app.MapGet("/v1/secure/ping", HandlePing);
            app.MapGet("/v1/secure/storage/shares", (HttpContext c) => Results.Json(RequireStorage().ListShares(PeerId(c))));
            app.MapGet("/v1/secure/storage/entries", (HttpContext c, string shareId, string? path) =>
                Results.Json(RequireStorage().ListEntries(PeerId(c), shareId, path ?? "")));
            app.MapMethods("/v1/secure/storage/content", ["GET", "HEAD"], HandleContent);
            app.MapMethods("/v1/secure/storage/thumbnail", ["GET", "HEAD"], (HttpContext c, string shareId, string path) =>
            {
                var local = RequireStorage().Resolve(PeerId(c), shareId, path, SharePermissions.Stream);
                var file = (Thumbnails ?? throw new IOException("썸네일이 준비되지 않았습니다.")).Thumbnail(local);
                return Results.File(file, "image/jpeg", lastModified: File.GetLastWriteTimeUtc(local),
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{Path.GetFileNameWithoutExtension(file)}\""));
            });
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or HttpListenerException or SocketException)
        {
            if (_app is not null)
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
            }

            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    private async Task ValidateClientCertificateAsync(HttpContext context, RequestDelegate next)
    {
        var certificate = context.Connection.ClientCertificate;
        if (certificate is null || !DeviceCertificateValidator.IsMeshDriveDeviceCertificate(certificate))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("장치 인증서가 필요합니다.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/v1/secure") &&
            !_coordinator.IsSecureClientAllowed(certificate))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("신뢰되지 않은 기기입니다.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private IResult HandleOfferAsync(HttpContext context, PairingOfferDto offer)
    {
        try
        {
            var certificate = context.Connection.ClientCertificate
                ?? throw new UnauthorizedAccessException("장치 인증서가 필요합니다.");
            var ipv4 = FormatReplyAddress(context.Connection.RemoteIpAddress);
            var response = _coordinator.AcceptOffer(offer, certificate, ipv4);
            return Results.Json(response);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private IResult HandleDecisionAsync(HttpContext context, PairingDecisionDto decision)
    {
        try
        {
            var certificate = context.Connection.ClientCertificate
                ?? throw new UnauthorizedAccessException("장치 인증서가 필요합니다.");
            _coordinator.AcceptRemoteDecision(decision, certificate);
            return Results.Ok();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static string? FormatReplyAddress(IPAddress? remote)
    {
        if (remote is null)
        {
            return null;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        return remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? remote.ToString()
            : null;
    }

    private IResult HandlePing() =>
        Results.Json(new SecurePingDto
        {
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
        });

    private StorageService RequireStorage() => Storage ?? throw new InvalidOperationException("공유 저장소가 준비되지 않았습니다.");
    private IResult HandleContent(HttpContext context, string shareId, string path, string? purpose)
    {
        var required = purpose == "download" ? SharePermissions.Download : SharePermissions.Stream;
        var local = RequireStorage().Resolve(PeerId(context), shareId, path, required);
        var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var modified = File.GetLastWriteTimeUtc(local);
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{stream.Length:x}-{modified.Ticks:x}\"");
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        return Results.File(stream, provider.TryGetContentType(local, out var mime) ? mime : "application/octet-stream",
            lastModified: modified, entityTag: etag, enableRangeProcessing: true);
    }
    private string PeerId(HttpContext context)
    {
        var fingerprint = DeviceFingerprints.FromCertificate(context.Connection.ClientCertificate!);
        return _coordinator.ListTrusted().FirstOrDefault(p => DeviceFingerprints.FixedEquals(p.Fingerprint, fingerprint))?.DeviceId
            ?? throw new UnauthorizedAccessException("신뢰되지 않은 기기입니다.");
    }
}
