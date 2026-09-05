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
    public FileTransferService? Transfers { get; init; }
    private CopyGrants? _copyGrants;
    public SyncFolders? Sync { get; init; }
    public SyncInbox? SyncInbox { get; init; }

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
            if (Storage is not null) _copyGrants = new(Storage);
            if (Transfers is not null) Transfers.IsRequesterTrusted = id => _coordinator.ListTrusted().Any(p => p.DeviceId == id);
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
            if (Sync is not null && SyncInbox is not null) SyncHttpApi.Map(app, Sync, SyncInbox, PeerId, () => Storage?.Paused == true);
            app.MapPost("/v1/pairing/decision", HandleDecisionAsync);
            app.MapGet("/v1/secure/ping", HandlePing);
            app.MapGet("/v1/secure/storage/shares", (HttpContext c) => Results.Json(RequireStorage().ListShares(PeerId(c))));
            app.MapGet("/v1/secure/storage/entries", (HttpContext c, string shareId, string? path) =>
                Results.Json(RequireStorage().ListEntries(PeerId(c), shareId, path ?? "")));
            app.MapMethods("/v1/secure/storage/content", ["GET", "HEAD"], HandleContent);
            app.MapGet("/v1/secure/storage/manifest", async (HttpContext c, string shareId, string path) =>
            {
                if (c.Request.Query.TryGetValue("copyToken", out var token)) ValidateCopyGrant(c, token.ToString(), shareId, path);
                return Results.Json(await QuickSendAdapter.ManifestAsync(RequireStorage().Resolve(PeerId(c), shareId, path, SharePermissions.Download), c.RequestAborted).ConfigureAwait(false));
            });
            app.MapGet("/v1/secure/storage/chunk", ReadChunkAsync);
            app.MapPost("/v1/secure/storage/copy-authorize", (HttpContext c, MeshDrive.Protocol.CopyGrantRequest request) =>
            {
                RequireTrusted(request.TargetDeviceId);
                return Results.Json(RequireCopyGrants().Create(PeerId(c), request));
            });
            app.MapGet("/v1/secure/storage/copy-grant", (HttpContext c, string token) => Results.Json(ValidateCopyGrant(c, token)));
            app.MapPost("/v1/secure/storage/copy-receive", async (HttpContext c, MeshDrive.Protocol.CopyReceiveRequest request) =>
                Results.Json(await RequireTransfers().ReceiveCopyAsync(PeerId(c), request, c.RequestAborted).ConfigureAwait(false)));
            app.MapGet("/v1/secure/storage/copy-progress", (HttpContext c, string id) => Results.Json(RequireTransfers().RemoteProgress(PeerId(c), id)));
            app.MapPost("/v1/secure/storage/upload-start", async (HttpContext c, MeshDrive.Protocol.UploadRequest request) =>
                Results.Json(await RequireTransfers().BeginUploadAsync(PeerId(c), request, c.RequestAborted).ConfigureAwait(false)));
            app.MapPut("/v1/secure/storage/upload-chunk", async (HttpContext c, Guid id) =>
            {
                if (c.Request.ContentLength is not long length || length > QuickSendAdapter.ChunkSize + 60 || length <= 60) return Results.BadRequest();
                var bytes = new byte[(int)length]; await c.Request.Body.ReadExactlyAsync(bytes, c.RequestAborted).ConfigureAwait(false);
                await RequireTransfers().ReceiveUploadAsync(PeerId(c), id, bytes, c.RequestAborted).ConfigureAwait(false); return Results.Ok();
            });
            app.MapPost("/v1/secure/storage/upload-complete", async (HttpContext c, Guid id) =>
            { await RequireTransfers().ReceiveUploadAsync(PeerId(c), id, null, c.RequestAborted).ConfigureAwait(false); return Results.Ok(); });
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
    private FileTransferService RequireTransfers() => Transfers ?? throw new IOException("전송 엔진이 준비되지 않았습니다.");
    private CopyGrants RequireCopyGrants() => _copyGrants ?? throw new IOException("직접 복사가 준비되지 않았습니다.");
    private void RequireTrusted(string id)
    {
        if (!_coordinator.ListTrusted().Any(p => p.DeviceId == id)) throw new UnauthorizedAccessException("복사 참여 기기 모두를 먼저 페어링하세요.");
    }
    private MeshDrive.Protocol.CopyGrant ValidateCopyGrant(HttpContext context, string token, string? shareId = null, string? path = null)
    {
        var grant = RequireCopyGrants().Validate(token, PeerId(context), shareId, path);
        RequireTrusted(grant.RequesterId);
        return grant;
    }
    private async Task<IResult> ReadChunkAsync(HttpContext c, string shareId, string path, long offset, Guid fileId, string version)
    {
        if (c.Request.Query.TryGetValue("copyToken", out var token)) ValidateCopyGrant(c, token.ToString(), shareId, path);
        var local = RequireStorage().Resolve(PeerId(c), shareId, path, SharePermissions.Download);
        await using var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if ($"{input.Length:x}-{File.GetLastWriteTimeUtc(local).Ticks:x}" != version) return Results.StatusCode(412);
        if (offset < 0 || offset >= input.Length || offset % QuickSendAdapter.ChunkSize != 0) return Results.BadRequest();
        input.Position = offset; var buffer = new byte[(int)Math.Min(QuickSendAdapter.ChunkSize, input.Length - offset)];
        await input.ReadExactlyAsync(buffer, c.RequestAborted).ConfigureAwait(false);
        return Results.Bytes(QuickSendAdapter.Pack(fileId, offset, buffer), "application/octet-stream");
    }
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
