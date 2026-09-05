using System.Net.Http.Json;
using System.Net.Security;
using MeshDrive.Core;

namespace MeshDrive.Agent;

public sealed class RemoteStorageClient(DeviceCredential credential, PairingCoordinator pairing)
{
    public async Task<T> GetAsync<T>(string deviceId, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(deviceId, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false) ?? throw new IOException("응답이 비어 있습니다.");
    }

    public async Task<HttpResponseMessage> SendAsync(string deviceId, HttpMethod method, string path,
        Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        var endpoint = pairing.GetTrustedEndpoint(deviceId);
        Exception? last = null;
        foreach (var address in endpoint.Addresses)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = [credential.Certificate],
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => credential.Certificate,
                    RemoteCertificateValidationCallback = (_, cert, _, errors) => DeviceCertificateValidator.AcceptTrusted(cert, errors, endpoint.Fingerprint),
                },
            };
            // The response owns this client until its streaming body is consumed.
            var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            var request = new HttpRequestMessage(method, $"https://{address}:{endpoint.Port}{path}");
            configure?.Invoke(request);
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.Content = new OwnedHttpContent(response.Content, client, handler, request);
                return response;
            }
            catch (HttpRequestException e) { client.Dispose(); handler.Dispose(); request.Dispose(); last = e; }
            catch { client.Dispose(); handler.Dispose(); request.Dispose(); throw; }
        }
        throw last ?? new IOException("상대 기기에 연결하지 못했습니다.");
    }

    public static string Resource(string kind, string shareId, string path) =>
        $"/v1/secure/storage/{kind}?shareId={Uri.EscapeDataString(shareId)}&path={Uri.EscapeDataString(path)}";

    private sealed class OwnedHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly IDisposable[] _owners;
        public OwnedHttpContent(HttpContent inner, params IDisposable[] owners)
        {
            _inner = inner; _owners = owners;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? -1; return length >= 0; }
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); foreach (var owner in _owners) owner.Dispose(); } base.Dispose(disposing); }
    }
}
