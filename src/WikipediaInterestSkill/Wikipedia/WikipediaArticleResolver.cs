using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Caching;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Wikipedia;

public interface IWikipediaArticleResolver
{
    Task<ResolvedArticle> ResolveAsync(
        string topic,
        string language,
        string sourceLanguage,
        string? articleOverride,
        CancellationToken cancellationToken,
        bool allowSearchFallback = false);
}

public sealed partial class WikipediaArticleResolver(
    WikimediaHttp http,
    PageviewCache? cache,
    ILogger<WikipediaArticleResolver> logger) : IWikipediaArticleResolver
{
    private readonly ConcurrentDictionary<(string Language, string Topic), Lazy<Task<SourceMatch?>>> _sources = new();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,19}$")]
    private static partial Regex LanguageCodePattern();

    public static bool IsValidLanguageCode(string code) => LanguageCodePattern().IsMatch(code);

    public async Task<ResolvedArticle> ResolveAsync(
        string topic,
        string language,
        string sourceLanguage,
        string? articleOverride,
        CancellationToken cancellationToken,
        bool allowSearchFallback = false)
    {
        if (!IsValidLanguageCode(language))
            throw new SkillException(ErrorCodes.UnknownLanguage,
                $"'{language}' is not a valid Wikipedia language code (expected a code such as en, pl, cs, uk or zh-yue).");

        try
        {
            var key = string.IsNullOrWhiteSpace(articleOverride)
                ? $"v2|topic|{sourceLanguage}|{language}|{(allowSearchFallback ? "fallback" : "strict")}|{topic.Trim().ToLowerInvariant()}"
                : $"v2|explicit|{language}|{articleOverride.Trim()}";
            if (cache?.GetResolution(key) is { } cached) return cached with { RequestedTopic = topic };

            var resolved = string.IsNullOrWhiteSpace(articleOverride)
                ? await ResolveTopicAsync(topic, language, sourceLanguage, allowSearchFallback, cancellationToken).ConfigureAwait(false)
                : await ResolveExplicitAsync(topic, language, articleOverride.Trim(), cancellationToken).ConfigureAwait(false);
            cache?.StoreResolution(key, resolved);
            logger.LogInformation("Resolved {Language}: '{Topic}' → '{Title}' via {Method} ({Confidence})", language, topic,
                resolved.ArticleTitle, resolved.Method, resolved.Confidence);
            return resolved;
        }
        catch (UnknownHostException ex)
        {
            throw new SkillException(ErrorCodes.UnknownLanguage,
                $"No Wikipedia edition exists for language code '{language}' ({ex.Host} not found).", ex);
        }
    }

    private async Task<ResolvedArticle> ResolveExplicitAsync(string topic, string language, string spec, CancellationToken ct)
    {
        var method = ResolutionMethod.ExplicitTitle;
        var title = spec;
        if (Uri.TryCreate(spec, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            method = ResolutionMethod.ExplicitUrl;
            var host = uri.Host.ToLowerInvariant();
            var urlLanguage = host.Split('.')[0];
            if (!host.EndsWith("wikipedia.org", StringComparison.Ordinal) || urlLanguage != language)
                throw new SkillException(ErrorCodes.InvalidArgument,
                    $"Article URL '{spec}' is not a {language}.wikipedia.org URL.");
            const string prefix = "/wiki/";
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new SkillException(ErrorCodes.InvalidArgument, $"Cannot read an article title from URL '{spec}'.");
            title = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]).Replace('_', ' ');
        }

        var page = await LookupAsync(language, title, includeLangLinks: false, ct).ConfigureAwait(false);
        if (page is null)
            throw new SkillException(ErrorCodes.UnresolvableArticle,
                $"The article '{title}' does not exist on {language}.wikipedia.org.");

        var notes = new List<string>
        {
            method == ResolutionMethod.ExplicitUrl ? "Article URL supplied by the user." : "Article title supplied by the user.",
        };
        if (page.RedirectedFrom is { } from) notes.Add($"'{from}' redirects to '{page.Title}'; pageviews are counted for the target.");
        var confidence = ResolutionConfidence.High;
        if (page.IsDisambiguation)
        {
            notes.Add("This is a disambiguation page, not an article about a single concept.");
            confidence = ResolutionConfidence.Low;
        }

        return new ResolvedArticle(language, topic, page.Title, ResolvedArticle.UrlFor(language, page.Title), method,
            confidence, Notes: notes);
    }

    private async Task<ResolvedArticle> ResolveTopicAsync(
        string topic, string language, string sourceLanguage, bool allowSearchFallback, CancellationToken ct)
    {
        SourceMatch? source;
        try
        {
            source = await _sources.GetOrAdd((sourceLanguage, topic.Trim().ToLowerInvariant()),
                _ => new Lazy<Task<SourceMatch?>>(() => FindInLanguageAsync(sourceLanguage, topic, CancellationToken.None))).Value
                .ConfigureAwait(false);
        }
        catch (UnknownHostException)
        {
            throw new SkillException(ErrorCodes.InvalidArgument, $"Unknown source language '{sourceLanguage}'.");
        }

        if (source is not null && sourceLanguage == language)
            return new ResolvedArticle(language, topic, source.Page.Title, ResolvedArticle.UrlFor(language, source.Page.Title),
                ResolutionMethod.Search, source.Confidence, null, source.Candidates, source.Notes);

        if (source is not null)
        {
            var linked = source.Page.LangLinks.TryGetValue(language, out var t)
                ? t
                : source.Page.MoreLangLinks
                    ? await LangLinkAsync(sourceLanguage, source.Page.Title, language, ct).ConfigureAwait(false)
                    : null;
            if (linked is not null)
            {
                var notes = new List<string>(source.Notes)
                {
                    $"Interlanguage link {sourceLanguage}:{source.Page.Title} → {language}:{linked} (same Wikidata item).",
                };
                return new ResolvedArticle(language, topic, linked, ResolvedArticle.UrlFor(language, linked),
                    ResolutionMethod.InterlanguageLink, source.Confidence, $"{sourceLanguage}:{source.Page.Title}",
                    source.Candidates, notes);
            }
        }

        var hits = await SearchAsync(language, topic, ct).ConfigureAwait(false);
        var why = source is null
            ? $"No {sourceLanguage}.wikipedia article matches '{topic}'"
            : $"{sourceLanguage}:{source.Page.Title} has no interlanguage link to {language}.wikipedia (no equivalent article is registered in Wikidata)";
        if (!allowSearchFallback || hits.Count == 0)
        {
            var candidates = hits.Count == 0
                ? $"A {language} search for '{topic}' found nothing."
                : $"Unverified {language} search results: {string.Join(", ", hits.Take(5).Select(h => $"\"{h}\""))}.";
            throw new SkillException(ErrorCodes.UnresolvableArticle,
                $"{why}. {candidates} Ask the user which {language} article (if any) represents the concept and re-run with " +
                $"--article {language}=\"Title\", or drop this language.");
        }

        var target = await FindInLanguageAsync(language, topic, ct, hits).ConfigureAwait(false)
                     ?? throw new SkillException(ErrorCodes.UnresolvableArticle, $"{why}; no usable {language} search result.");
        var fallbackNotes = new List<string> { $"{why}; used the best {language} search result. Conceptual equivalence is NOT verified." };
        fallbackNotes.AddRange(target.Notes);
        return new ResolvedArticle(language, topic, target.Page.Title, ResolvedArticle.UrlFor(language, target.Page.Title),
            ResolutionMethod.TargetLanguageSearchFallback, ResolutionConfidence.Low,
            source is null ? null : $"{sourceLanguage}:{source.Page.Title}", hits.Where(h => h != target.Page.Title).Take(4).ToList(),
            fallbackNotes);
    }

    private sealed record SourceMatch(PageInfo Page, ResolutionConfidence Confidence, IReadOnlyList<string> Candidates,
        IReadOnlyList<string> Notes);

    private sealed record PageInfo(
        string Title,
        bool IsDisambiguation,
        string? RedirectedFrom,
        IReadOnlyDictionary<string, string> LangLinks,
        bool MoreLangLinks);

    private async Task<SourceMatch?> FindInLanguageAsync(
        string language, string topic, CancellationToken ct, IReadOnlyList<string>? knownHits = null)
    {
        var exact = await LookupAsync(language, topic, includeLangLinks: true, ct).ConfigureAwait(false);
        if (exact is { IsDisambiguation: false })
        {
            var note = exact.RedirectedFrom is { } from
                ? $"'{from}' redirects to the {language} article '{exact.Title}'."
                : $"'{topic}' matches the {language} article title exactly.";
            return new SourceMatch(exact, ResolutionConfidence.High, Array.Empty<string>(), new[] { note });
        }

        var hits = knownHits ?? await SearchAsync(language, topic, ct).ConfigureAwait(false);
        var usable = await FirstArticleAsync(language, hits, ct).ConfigureAwait(false);
        if (usable is { } hit)
        {
            var page = await LookupAsync(language, hit, includeLangLinks: true, ct).ConfigureAwait(false);
            if (page is null) return null;
            var notes = new List<string>
            {
                $"No article titled '{topic}' on {language}.wikipedia; selected the top search result '{page.Title}'. Check that it is the intended concept.",
            };
            if (exact is { IsDisambiguation: true }) notes.Add($"'{exact.Title}' is a disambiguation page.");
            return new SourceMatch(page, ResolutionConfidence.Medium, hits.Where(h => h != hit).Take(4).ToList(), notes);
        }

        return null;
    }

    private async Task<string?> FirstArticleAsync(string language, IReadOnlyList<string> titles, CancellationToken ct)
    {
        if (titles.Count == 0) return null;
        var url = Api(language,
            "action=query&format=json&formatversion=2&redirects=1&prop=pageprops&ppprop=disambiguation&titles=" +
            Uri.EscapeDataString(string.Join("|", titles)));
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
            return null;

        var renames = new Dictionary<string, string>();
        foreach (var key in new[] { "normalized", "redirects" })
            if (query.TryGetProperty(key, out var list))
                foreach (var item in list.EnumerateArray())
                    renames[item.GetProperty("from").GetString()!] = item.GetProperty("to").GetString()!;

        var articles = pages.EnumerateArray()
            .Where(p => !p.TryGetProperty("missing", out _) && !p.TryGetProperty("invalid", out _) &&
                        (!p.TryGetProperty("ns", out var ns) || ns.GetInt32() == 0) &&
                        !(p.TryGetProperty("pageprops", out var pp) && pp.TryGetProperty("disambiguation", out _)))
            .Select(p => p.GetProperty("title").GetString()!)
            .ToHashSet();

        foreach (var title in titles)
        {
            var final = title;
            for (var hop = 0; hop < 3 && renames.TryGetValue(final, out var next); hop++) final = next;
            if (articles.Contains(final)) return title;
        }

        return null;
    }

    private async Task<PageInfo?> LookupAsync(string language, string title, bool includeLangLinks, CancellationToken ct)
    {
        var props = includeLangLinks ? "pageprops%7Clanglinks&lllimit=max" : "pageprops";
        var url = Api(language,
            $"action=query&format=json&formatversion=2&redirects=1&prop={props}&ppprop=disambiguation&titles={Uri.EscapeDataString(title)}");
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
            return null;
        var page = pages.EnumerateArray().FirstOrDefault();
        if (page.ValueKind != JsonValueKind.Object || page.TryGetProperty("missing", out _) ||
            page.TryGetProperty("invalid", out _))
            return null;
        if (page.TryGetProperty("ns", out var ns) && ns.GetInt32() != 0) return null;

        string? redirectedFrom = null;
        if (query.TryGetProperty("redirects", out var redirects))
            redirectedFrom = redirects.EnumerateArray().Select(r => r.GetProperty("from").GetString()).FirstOrDefault();
        var isDisambiguation = page.TryGetProperty("pageprops", out var pp) && pp.TryGetProperty("disambiguation", out _);
        var links = new Dictionary<string, string>();
        if (page.TryGetProperty("langlinks", out var ll))
            foreach (var link in ll.EnumerateArray())
                links[link.GetProperty("lang").GetString()!] = link.GetProperty("title").GetString()!;
        var more = doc.RootElement.TryGetProperty("continue", out var cont) && cont.TryGetProperty("llcontinue", out _);
        return new PageInfo(page.GetProperty("title").GetString()!, isDisambiguation, redirectedFrom, links, more);
    }

    private async Task<IReadOnlyList<string>> SearchAsync(string language, string topic, CancellationToken ct)
    {
        var url = Api(language,
            $"action=query&format=json&formatversion=2&list=search&srnamespace=0&srlimit=5&srsearch={Uri.EscapeDataString(topic)}");
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("search", out var search))
            return Array.Empty<string>();
        return search.EnumerateArray().Select(s => s.GetProperty("title").GetString()!).ToList();
    }

    private async Task<string?> LangLinkAsync(string sourceLanguage, string title, string targetLanguage, CancellationToken ct)
    {
        var url = Api(sourceLanguage,
            $"action=query&format=json&formatversion=2&prop=langlinks&lllang={Uri.EscapeDataString(targetLanguage)}&titles={Uri.EscapeDataString(title)}");
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
            return null;
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("langlinks", out var links)) continue;
            foreach (var link in links.EnumerateArray())
                if (link.GetProperty("lang").GetString() == targetLanguage)
                    return link.GetProperty("title").GetString();
        }

        return null;
    }

    private static Uri Api(string language, string query) => new($"https://{language}.wikipedia.org/w/api.php?{query}");

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken ct)
    {
        var (status, body) = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
            throw new SkillException(ErrorCodes.UnknownLanguage, $"{url.Host} returned HTTP 404; is the language code correct?");
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new SkillException(ErrorCodes.WikimediaApiFailure,
                    $"MediaWiki API error from {url.Host}: {error.GetProperty("info").GetString()}");
            return doc;
        }
        catch (JsonException ex)
        {
            throw new SkillException(ErrorCodes.WikimediaApiFailure, $"Unexpected response from {url.Host}.", ex);
        }
    }
}
