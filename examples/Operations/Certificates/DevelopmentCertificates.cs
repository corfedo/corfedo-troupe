using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Corfedo.Troupe.Examples.Operations;

/// <summary>Writes a development root, a loopback server certificate and one client certificate per caller as PEM files.</summary>
public static class DevelopmentCertificates
{
    public static void Write(string directory, params string[] callers)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Troupe operations example root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(30));
        File.WriteAllText(Path.Combine(directory, "root.pem"), root.ExportCertificatePem());

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.IPv6Loopback);
        Issue(directory, "server", root, now, "1.3.6.1.5.5.7.3.1", names.Build());
        foreach (var caller in callers) Issue(directory, caller, root, now, "1.3.6.1.5.5.7.3.2", null);
    }

    private static void Issue(string directory, string name, X509Certificate2 root, DateTimeOffset now, string usage, X509Extension? names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        if (names is not null) request.CertificateExtensions.Add(names);
        using var certificate = request.Create(root, now.AddMinutes(-5), now.AddDays(30), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(directory, $"{name}.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, $"{name}.key"), key.ExportPkcs8PrivateKeyPem());
    }
}
