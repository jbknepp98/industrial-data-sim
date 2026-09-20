using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class HistorianTlsTests
{
    [Theory]
    [InlineData("localhost", true, true)]
    [InlineData("wrong.invalid", true, false)]
    [InlineData("localhost", false, false)]
    public async Task CustomTrustRequiresCorrectRootAndHostname(string hostname, bool trustedRoot, bool succeeds)
    {
        using var files = new RuntimeFixture();
        Directory.CreateDirectory(files.Folder);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=" + hostname, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostname);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var issued = leafRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var leaf = issued.CopyWithPrivateKey(leafKey);
        string pem = Path.Combine(files.Folder, "trust.pem");
        File.WriteAllText(pem, root.ExportCertificatePem());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            try
            {
                using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
                using var tls = new SslStream(tcp.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = leaf }, deadline.Token);
                using var reader = new StreamReader(tls, leaveOpen: true);
                while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                const string body = "{\"access_token\":\"synthetic\",\"expires_in\":3600}";
                byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                await tls.WriteAsync(response, deadline.Token);
                await tls.FlushAsync(deadline.Token);
            }
            catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
        });
        using var client = new HistorianClient(new HistorianConnection {
            Profile = "tls-test", Historian = new($"https://localhost:{port}/"), Pulse = new($"https://localhost:{port}/"),
            ClientId = "synthetic", ClientSecret = "synthetic", Audience = "Historian", CaFile = trustedRoot ? pem : null });
        if (succeeds) await client.Authenticate(deadline.Token);
        else await Assert.ThrowsAsync<HttpRequestException>(() => client.Authenticate(deadline.Token));
        await server;
    }
}
