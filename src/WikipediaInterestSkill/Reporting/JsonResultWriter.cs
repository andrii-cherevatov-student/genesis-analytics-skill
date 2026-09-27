using System.Text.Json;
using WikipediaInterestSkill.Application;

namespace WikipediaInterestSkill.Reporting;

public static class JsonResultWriter
{
    public static void Write(string path, AnalysisResult result)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(result));
        File.Move(tmp, path, overwrite: true);
    }

    public static string Serialize(AnalysisResult result) => JsonSerializer.Serialize(result, JsonOptions.Default);
}
