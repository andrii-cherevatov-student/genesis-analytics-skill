using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Application;

namespace WikipediaInterestSkill.Wikipedia;

public sealed record HttpRetryOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxConcurrency { get; init; } = 6;
}

public sealed class UnknownHostException(string host, Exception inner)
    : Exception($"Host '{host}' does not exist.", inner)
{
    public string Host { get; } = host;
}

public sealed class WikimediaHttp : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly HttpRetryOptions _options;
    private readonly SemaphoreSlim _gate;
    public WikimediaHttp(ILogger logger, HttpRetryOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new HttpRetryOptions();
        _logger = logger;
        _gate = new SemaphoreSlim(_options.MaxConcurrency);
        handler ??= new SocketsHttpHandler
        {
            MaxConnectionsPerServer = _options.MaxConcurrency,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Add("Api-User-Agent", UserAgent);
    }

    public static string UserAgent
    {
        get
        {
            var contact = Environment.GetEnvironmentVariable("WIKI_INTEREST_CONTACT");
            var version = AnalysisPipeline.ToolVersion;
            return string.IsNullOrWhiteSpace(contact)
                ? $"WikipediaInterestSkill/{version} (AI agent skill for Wikipedia pageview trend analysis; .NET HttpClient)"
                : $"WikipediaInterestSkill/{version} ({contact.Trim()}; AI agent skill for Wikipedia pageview trend analysis)";
        }
    }

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = _options.InitialDelay;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? retryAfter = null;
            string failure;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                _logger.LogDebug("HTTP {Status} {Host}{Path} in {ElapsedMs} ms", (int)response.StatusCode, url.Host,
                    url.AbsolutePath, sw.ElapsedMilliseconds);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                    return (response.StatusCode, body);

                var status = (int)response.StatusCode;
                if (status != 429 && status < 500)
                    throw new SkillException(ErrorCodes.WikimediaApiFailure,
                        $"Wikimedia API returned HTTP {status} for {url.Host}{url.AbsolutePath}: {Truncate(body)}");
                retryAfter = response.Headers.RetryAfter?.Delta;
                failure = $"HTTP {status}";
            }
            catch (HttpRequestException ex) when (IsUnknownHost(ex))
            {
                throw new UnknownHostException(url.Host, ex);
            }
            catch (HttpRequestException ex)
            {
                failure = ex.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = $"timeout after {_options.RequestTimeout.TotalSeconds:0}s";
            }
            finally
            {
                _gate.Release();
            }

            attempt++;
            if (attempt > _options.MaxRetries)
                throw new SkillException(ErrorCodes.WikimediaApiFailure,
                    $"Wikimedia API request to {url.Host} failed after {attempt} attempts ({failure}).");

            var wait = retryAfter is { } ra && ra < _options.MaxDelay ? ra : delay;
            _logger.LogWarning("Transient failure ({Failure}) for {Host}; retry {Attempt}/{Max} in {DelayMs} ms", failure,
                url.Host, attempt, _options.MaxRetries, (int)wait.TotalMilliseconds);
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _options.MaxDelay.TotalMilliseconds));
        }
    }

    private static bool IsUnknownHost(HttpRequestException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData };

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
