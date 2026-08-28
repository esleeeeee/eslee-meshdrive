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
            app.MapPost("/v1/pairing/offer", HandleOfferAsync);
            app.MapPost("/v1/pairing/decision", HandleDecisionAsync);
            app.MapGet("/v1/secure/ping", HandlePing);
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
}
