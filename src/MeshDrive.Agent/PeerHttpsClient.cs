using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed class PeerHttpsClient(DeviceCredential credential)
{
    public Task<PairingOfferDto> OfferAsync(string ipv4, int port, PairingOfferDto offer, CancellationToken cancellationToken) =>
        SendAsync<PairingOfferDto, PairingOfferDto>(ipv4, port, HttpMethod.Post, "/v1/pairing/offer", offer, trustedFingerprint: null, cancellationToken);

    public Task DecideAsync(string ipv4, int port, PairingDecisionDto decision, CancellationToken cancellationToken) =>
        SendAsync<PairingDecisionDto, object>(ipv4, port, HttpMethod.Post, "/v1/pairing/decision", decision, trustedFingerprint: null, cancellationToken);

    public Task<SecurePingDto> PingAsync(string ipv4, int port, string fingerprint, CancellationToken cancellationToken) =>
        SendAsync<object, SecurePingDto>(ipv4, port, HttpMethod.Get, "/v1/secure/ping", body: null, fingerprint, cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string ipv4,
        int port,
        HttpMethod method,
        string path,
        TRequest? body,
        string? trustedFingerprint,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "meshdrive.local",
                ClientCertificates = [credential.Certificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => credential.Certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    trustedFingerprint is null
                        ? DeviceCertificateValidator.AcceptForPairing(certificate, errors)
                        : DeviceCertificateValidator.AcceptTrusted(certificate, errors, trustedFingerprint),
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(method, new Uri($"https://{ipv4}:{port}{path}"));
        if (body is not null && method != HttpMethod.Get)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"HTTPS {path} 실패: {(int)response.StatusCode} {detail}");
        }

        if (typeof(TResponse) == typeof(object))
        {
            return default!;
        }

        var payload = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return payload ?? throw new InvalidOperationException("HTTPS 응답이 비어 있습니다.");
    }
}
