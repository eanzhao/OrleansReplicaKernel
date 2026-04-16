using System.Security.Cryptography.X509Certificates;
using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Security;

public sealed class TcpTransportSecurityOptions
{
    private readonly Dictionary<string, TrustedPeer> _trustedPeersByName;
    private readonly Dictionary<string, TrustedPeer> _trustedPeersByThumbprint;

    public TcpTransportSecurityOptions(
        X509Certificate2 localCertificate,
        IEnumerable<TrustedPeer> trustedPeers)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(trustedPeers);

        LocalCertificate = localCertificate;
        _trustedPeersByName = trustedPeers.ToDictionary(
            item => item.Name,
            item => item,
            StringComparer.Ordinal);
        _trustedPeersByThumbprint = _trustedPeersByName.Values.ToDictionary(
            item => item.CertificateThumbprint,
            item => item,
            StringComparer.OrdinalIgnoreCase);
    }

    public X509Certificate2 LocalCertificate { get; }

    public bool TryGetTrustedPeer(string name, out TrustedPeer peer)
        => _trustedPeersByName.TryGetValue(name, out peer!);

    public bool TryMatchTrustedPeer(X509Certificate2? certificate, out TrustedPeer peer)
    {
        var thumbprint = NormalizeThumbprint(certificate?.Thumbprint);
        if (thumbprint.Length == 0)
        {
            peer = null!;
            return false;
        }

        return _trustedPeersByThumbprint.TryGetValue(thumbprint, out peer!);
    }

    public static string NormalizeThumbprint(string? thumbprint)
        => string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    public sealed record TrustedPeer(
        string Name,
        InvocationSourceKind SourceKind,
        string CertificateThumbprint,
        string? CertificateSubject)
    {
        public static TrustedPeer Create(
            string name,
            InvocationSourceKind sourceKind,
            X509Certificate2 certificate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(certificate);

            return new TrustedPeer(
                name,
                sourceKind,
                NormalizeThumbprint(certificate.Thumbprint),
                certificate.Subject);
        }
    }
}
