using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace IndustrialDataSim.Runtime;

/// <summary>Deployment-only configuration. Never serialize this object or include it in model state.</summary>
public sealed class HistorianConnection
{
    public required string Profile { get; init; }
    public required Uri Historian { get; init; }
    public required Uri Pulse { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string Audience { get; init; }
    public string? CaFile { get; init; }

    public static HistorianConnection FromEnvironment()
    {
        string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value :
            throw new RuntimeFailure("connection.missing_setting", $"Set {key} in the process environment. Keep credentials outside model files, SQLite and source control.");
        Uri Origin(string key)
        {
            if (!Uri.TryCreate(Required(key), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new RuntimeFailure("connection.invalid_origin", $"{key} must be an HTTPS origin with no path, query, fragment or embedded credentials. Check the service address.");
            return uri;
        }
        var profile = Required("TIMEBASE_PROFILE");
        if (!Regex.IsMatch(profile, "^[A-Za-z0-9_-]{1,64}$"))
            throw new RuntimeFailure("connection.invalid_profile", "TIMEBASE_PROFILE must be a model connection-profile identifier of 1–64 letters, digits, hyphens or underscores.");
        return new() { Profile = profile, Historian = Origin("TIMEBASE_BASE_URL"), Pulse = Origin("TIMEBASE_PULSE_URL"),
            ClientId = Required("TIMEBASE_CLIENT_ID"), ClientSecret = Required("TIMEBASE_CLIENT_SECRET"),
            Audience = Required("TIMEBASE_AUDIENCE"), CaFile = Environment.GetEnvironmentVariable("TIMEBASE_CA_BUNDLE") };
    }

    internal void Validate()
    {
        foreach (var origin in new[] { Historian, Pulse })
            if (!origin.IsAbsoluteUri || origin.Scheme != "https" || origin.AbsolutePath != "/" ||
                origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                throw new RuntimeFailure("connection.invalid_origin", "Use HTTPS service origins without paths, queries or embedded credentials. Do not disable TLS verification.");
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(Audience))
            throw new RuntimeFailure("connection.missing_setting", "Supply client ID, secret and audience through deployment configuration. Never put them in model files or state.");
    }

    internal HttpClient CreateClient()
    {
        Validate();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        // A custom root replaces trust anchors, never hostname or validity checks.
        // Redirects are disabled so neither bearer tokens nor client secrets can
        // be forwarded to another origin, and a POST is never retried on 401.
        if (!string.IsNullOrEmpty(CaFile))
        {
            var roots = new X509Certificate2Collection();
            try { roots.ImportFromPemFile(CaFile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { handler.Dispose(); throw new RuntimeFailure("connection.ca_file", "Cannot read the PEM trust bundle. Check its format, permissions and root CA; do not disable TLS verification."); }
            if (roots.Count == 0) { handler.Dispose(); throw new RuntimeFailure("connection.ca_file", "The PEM trust bundle contains no certificates. Export the trusted root CA in PEM format."); }
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(certificate);
            };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
