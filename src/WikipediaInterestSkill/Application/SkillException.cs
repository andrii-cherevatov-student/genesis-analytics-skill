namespace WikipediaInterestSkill.Application;

public static class ErrorCodes
{
    public const string UnknownLanguage = "UnknownLanguage";
    public const string UnresolvableArticle = "UnresolvableArticle";
    public const string InsufficientData = "InsufficientData";
    public const string InvalidDateRange = "InvalidDateRange";
    public const string WikimediaApiFailure = "WikimediaApiFailure";
    public const string InvalidOutputDirectory = "InvalidOutputDirectory";
    public const string InvalidArgument = "InvalidArgument";
    public const string AllLanguagesFailed = "AllLanguagesFailed";
    public const string UnexpectedError = "UnexpectedError";
}

public class SkillException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;

    public static (string Code, string Message) Describe(Exception ex) => ex switch
    {
        SkillException skill => (skill.Code, skill.Message),
        _ => (ErrorCodes.UnexpectedError, $"{ex.GetType().Name}: {ex.Message.FirstLine()}"),
    };
}
