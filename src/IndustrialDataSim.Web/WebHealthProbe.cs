namespace IndustrialDataSim.Web;

/// <summary>
/// `flow-data-simulator-web healthcheck`: the container's Docker health check.
///
/// It asks the running page for /api/health on loopback and succeeds when the
/// page answers, even when the answer is "degraded". A missing Gage model or an
/// unreachable database is explained on the page and in /api/health; marking
/// the container unhealthy for it would not fix it. The check never runs a
/// model call (it does not pass refresh=true), so it costs no tokens. The image
/// has no curl or wget, which is why the program checks itself.
/// </summary>
public static class WebHealthProbe
{
    /// <summary>The loopback health URL for the first listen address in FLOWSIM_WEB_URLS.</summary>
    public static Uri HealthUrl(string? urls)
    {
        string first = (urls ?? new WebOptions().Urls)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? new WebOptions().Urls;
        // Kestrel accepts wildcard hosts (+, *) that Uri does not parse.
        string parsable = first.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0");
        if (!Uri.TryCreate(parsable, UriKind.Absolute, out var listen))
            throw new ArgumentException($"FLOWSIM_WEB_URLS starts with \"{first}\", which is not an address like http://0.0.0.0:8480.");
        string host = listen.Host is "0.0.0.0" or "[::]" or "::" ? "127.0.0.1" : listen.Host;
        return new UriBuilder(listen.Scheme, host, listen.Port, "/api/health").Uri;
    }

    /// <summary>Exit code 0 when the page answers /api/health with 200, otherwise 1, with one line on stderr.</summary>
    public static async Task<int> Run(string? urls, TextWriter error)
    {
        Uri target;
        try { target = HealthUrl(urls); }
        catch (ArgumentException failure)
        {
            await error.WriteLineAsync(failure.Message);
            return 1;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            // The page only answers on allowed host names; localhost is always among the defaults.
            request.Headers.Host = target.Host == "127.0.0.1" ? "localhost" : null;
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return 0;
            await error.WriteLineAsync($"The page answered {(int)response.StatusCode} at {target}.");
            return 1;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            await error.WriteLineAsync($"The page did not answer at {target}: {failure.Message}");
            return 1;
        }
    }
}
