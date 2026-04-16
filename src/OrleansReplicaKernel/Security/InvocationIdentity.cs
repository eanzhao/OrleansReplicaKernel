using System.Security.Cryptography.X509Certificates;
using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Security;

public sealed record InvocationIdentity(
    InvocationSourceKind SourceKind,
    string Name,
    bool IsAuthenticated,
    string? CertificateThumbprint = null,
    string? CertificateSubject = null)
{
    public static InvocationIdentity CreateLocal(string name, InvocationSourceKind sourceKind)
        => new(sourceKind, name, IsAuthenticated: false);

    public static InvocationIdentity CreateAuthenticated(
        string name,
        InvocationSourceKind sourceKind,
        X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(certificate);

        return new InvocationIdentity(
            sourceKind,
            name,
            IsAuthenticated: true,
            TcpTransportSecurityOptions.NormalizeThumbprint(certificate.Thumbprint),
            certificate.Subject);
    }
}
