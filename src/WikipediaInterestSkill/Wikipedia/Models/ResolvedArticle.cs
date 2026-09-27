using System.Text.Json.Serialization;

namespace WikipediaInterestSkill.Wikipedia.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ResolutionMethod>))]
public enum ResolutionMethod
{
    ExplicitUrl,
    ExplicitTitle,
    Search,
    InterlanguageLink,
    TargetLanguageSearchFallback,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResolutionConfidence>))]
public enum ResolutionConfidence
{
    High,
    Medium,
    Low,
}

public sealed record ResolvedArticle(
    string Language,
    string RequestedTopic,
    string ArticleTitle,
    Uri ArticleUrl,
    ResolutionMethod Method,
    ResolutionConfidence Confidence,
    string? SourceArticle = null,
    IReadOnlyList<string>? Candidates = null,
    IReadOnlyList<string>? Notes = null)
{
    public string Project => $"{Language}.wikipedia";

    public static Uri UrlFor(string language, string title) =>
        new($"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/")}");
}
