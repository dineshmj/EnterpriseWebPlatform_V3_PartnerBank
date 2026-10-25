using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace PartnerBank.Security;

/// <summary>
/// The HTTP handler a Partner Bank deployable uses to call its OWN authorization server (discovery,
/// keys, token): it trusts only the PoC's root, and may resolve the Partner's host names to a fixed
/// address so the home PC needs no hosts-file entries.
/// </summary>
public static class TrustedBackchannel
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The authorization server's certificate must chain to one of the given roots (custom
    // │ trust: the PoC's root, and in a cluster the public CA the Gateway's certificate comes
    // │ from - TlsOptions.LoadBackchannelTrustRoots) AND match the host name - a server with
    // │ another certificate is never believed, even one the operating system would trust.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static HttpMessageHandler Create(X509Certificate2Collection trustRoots, string? resolvePartnerHostsTo) =>
        new SocketsHttpHandler
        {
            ConnectCallback = string.IsNullOrWhiteSpace(resolvePartnerHostsTo)
                ? null
                : async (context, cancellationToken) =>
                {
                    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
                    // │ The Partner's own deployables run on the same machine: its host names are
                    // │ resolved to the configured address (127.0.0.1) instead of through DNS.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    var target = context.DnsEndPoint.Host.EndsWith(".ewp.home.arpa", StringComparison.OrdinalIgnoreCase)
                        ? new IPEndPoint(IPAddress.Parse(resolvePartnerHostsTo), context.DnsEndPoint.Port)
                        : (EndPoint)context.DnsEndPoint;
                    await socket.ConnectAsync(target, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    certificate is not null &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                    new X509Certificate2(certificate).ChainsToAny(trustRoots, chain)
            }
        };
}