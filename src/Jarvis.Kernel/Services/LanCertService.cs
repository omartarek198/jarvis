using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// Self-signed certificate for the LAN voice endpoint (getUserMedia on phones requires a
/// secure origin). Generated in pure .NET at data/lan-cert.pfx, SANs = hostname + current
/// LAN IPs. Phones must trust it once (or use mkcert for a smoother story — docs/PHONE.md).
/// </summary>
public static class LanCertService
{
    public static X509Certificate2 LoadOrCreate(SecretsService secrets, ILogger log)
    {
        var path = Path.Combine(JarvisPaths.Data, "lan-cert.pfx");
        var pass = secrets.Get("lan.certpass");
        if (pass is null)
        {
            pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            secrets.Set("lan.certpass", pass);
        }

        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, pass);
                if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
            }
            catch (Exception ex) { log.LogWarning("[lan] existing cert unusable ({Msg}) — regenerating", ex.Message); }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=jarvis-{Environment.MachineName.ToLowerInvariant()}",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName.ToLowerInvariant());
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LanAddresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));   // serverAuth

        var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, pass));
        log.LogInformation("[lan] self-signed cert generated at {Path} (SANs: {Ips})",
            path, string.Join(", ", LanAddresses()));
        return X509CertificateLoader.LoadPkcs12FromFile(path, pass);
    }

    public static IEnumerable<IPAddress> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);
}
