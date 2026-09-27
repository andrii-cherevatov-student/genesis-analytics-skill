using System.Globalization;

namespace WikipediaInterestSkill;

public static class Format
{
    public static string Pct(double value) =>
        Sign(value) + Math.Abs(value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string Pct0(double value) =>
        Sign(value) + Math.Abs(Math.Round(value * 100, MidpointRounding.AwayFromZero)).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Threshold(double value) => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static string FirstLine(this string text) => text.Split('\n')[0].Trim();

    private static string Sign(double value) => value >= 0 ? "+" : "−";
}
