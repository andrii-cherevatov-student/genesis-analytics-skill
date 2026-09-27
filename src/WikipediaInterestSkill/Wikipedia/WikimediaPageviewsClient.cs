using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Wikipedia;

public interface IWikimediaPageviewsClient
{
    Task<IReadOnlyList<DailyPageview>> GetPageviewsAsync(
        ResolvedArticle article,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);
}

public sealed class WikimediaPageviewsClient(WikimediaHttp http, ILogger<WikimediaPageviewsClient> logger)
    : IWikimediaPageviewsClient
{
    public static readonly DateOnly FirstAvailableDate = new(2015, 7, 1);

    public static Uri BuildUrl(string project, string title, DateOnly from, DateOnly to)
    {
        var encoded = Uri.EscapeDataString(title.Replace(' ', '_'));
        return new Uri(
            $"https://wikimedia.org/api/rest_v1/metrics/pageviews/per-article/{project}/all-access/user/{encoded}/daily/" +
            $"{from:yyyyMMdd}00/{to:yyyyMMdd}00");
    }

    public async Task<IReadOnlyList<DailyPageview>> GetPageviewsAsync(
        ResolvedArticle article,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (to < from) return Array.Empty<DailyPageview>();
        if (from < FirstAvailableDate) from = FirstAvailableDate;
        var sw = Stopwatch.StartNew();
        var url = BuildUrl(article.Project, article.ArticleTitle, from, to);
        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (UnknownHostException ex)
        {
            throw new SkillException(ErrorCodes.WikimediaApiFailure, $"Cannot reach the Wikimedia API: {ex.Message}", ex);
        }

        if (status == HttpStatusCode.NotFound)
        {
            logger.LogInformation("No pageview data for {Project}/{Article} {From}..{To} (HTTP 404)", article.Project,
                article.ArticleTitle, from, to);
            return Array.Empty<DailyPageview>();
        }

        var result = Parse(body);
        logger.LogInformation("Fetched {Days} days for {Project}/{Article} {From}..{To} in {ElapsedMs} ms", result.Count,
            article.Project, article.ArticleTitle, from, to, sw.ElapsedMilliseconds);
        return result;
    }

    internal static IReadOnlyList<DailyPageview> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return Array.Empty<DailyPageview>();
            var list = new List<DailyPageview>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                var ts = item.GetProperty("timestamp").GetString() ?? throw new InvalidDataException("Missing timestamp.");
                var date = DateOnly.ParseExact(ts[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
                var views = item.GetProperty("views").GetInt64();
                if (views < 0) throw new InvalidDataException($"Negative views on {date}.");
                list.Add(new DailyPageview(date, views));
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            throw new SkillException(ErrorCodes.WikimediaApiFailure, $"Unexpected pageviews API response: {ex.Message}", ex);
        }
    }
}
