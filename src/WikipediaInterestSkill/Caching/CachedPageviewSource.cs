using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Wikipedia;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Caching;

public sealed class CachedPageviewSource(
    IWikimediaPageviewsClient client,
    PageviewCache cache,
    ILogger<CachedPageviewSource> logger)
{
    public async Task<IReadOnlyList<DailyPageview>> GetAsync(
        ResolvedArticle article, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var missing = cache.MissingRanges(article.Project, article.ArticleTitle, from, to);
        var fetched = new List<DailyPageview>();
        foreach (var (f, t) in missing)
        {
            var daily = await client.GetPageviewsAsync(article, f, t, cancellationToken).ConfigureAwait(false);
            cache.Store(article.Project, article.ArticleTitle, f, t, daily);
            fetched.AddRange(daily);
        }

        var result = missing.Count == 1 && missing[0] == (from, to)
            ? fetched.OrderBy(d => d.Date).ToList()
            : cache.Get(article.Project, article.ArticleTitle, from, to);
        logger.LogDebug("{Days} cached days for {Project}/{Article}", result.Count, article.Project, article.ArticleTitle);
        return result;
    }
}
