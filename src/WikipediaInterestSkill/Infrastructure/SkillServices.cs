using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Caching;
using WikipediaInterestSkill.Wikipedia;

namespace WikipediaInterestSkill.Infrastructure;

public sealed class SkillServices : IDisposable
{
    private readonly WikimediaHttp _http;

    public SkillServices(ILoggerFactory loggerFactory, string cacheDirectory, AnalysisSettings settings,
        HttpMessageHandler? handler = null, HttpRetryOptions? retry = null, Func<DateOnly>? today = null)
    {
        _http = new WikimediaHttp(loggerFactory.CreateLogger<WikimediaHttp>(), retry, handler);
        var cache = new PageviewCache(cacheDirectory, loggerFactory.CreateLogger<PageviewCache>(), today);
        Resolver = new WikipediaArticleResolver(_http, cache, loggerFactory.CreateLogger<WikipediaArticleResolver>());
        var client = new WikimediaPageviewsClient(_http, loggerFactory.CreateLogger<WikimediaPageviewsClient>());
        Source = new CachedPageviewSource(client, cache, loggerFactory.CreateLogger<CachedPageviewSource>());
        Pipeline = new AnalysisPipeline(Resolver, Source, cache, settings, loggerFactory, today);
    }

    public IWikipediaArticleResolver Resolver { get; }
    public CachedPageviewSource Source { get; }
    public IAnalysisPipeline Pipeline { get; }

    public static string ResolveCacheDirectory(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory)) return Path.GetFullPath(explicitDirectory);
        var env = Environment.GetEnvironmentVariable("WIKI_INTEREST_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.Combine(FindSkillRoot() ?? AppContext.BaseDirectory, ".cache");
    }

    public static string? FindSkillRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SKILL.md"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
