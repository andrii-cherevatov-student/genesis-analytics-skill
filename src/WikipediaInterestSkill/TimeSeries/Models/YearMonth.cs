using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WikipediaInterestSkill.TimeSeries.Models;

[JsonConverter(typeof(YearMonthJsonConverter))]
public readonly record struct YearMonth : IComparable<YearMonth>
{
    public YearMonth(int year, int month)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        Year = year;
        Month = month;
    }

    public int Year { get; }
    public int Month { get; }

    public static YearMonth From(DateOnly date) => new(date.Year, date.Month);

    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => new(Year, Month, DaysInMonth);
    public int DaysInMonth => DateTime.DaysInMonth(Year, Month);

    public int Ordinal => Year * 12 + (Month - 1);

    public static YearMonth FromOrdinal(int ordinal) => new(ordinal / 12, ordinal % 12 + 1);

    public YearMonth AddMonths(int months) => FromOrdinal(Ordinal + months);

    public int MonthsSince(YearMonth other) => Ordinal - other.Ordinal;

    public int CompareTo(YearMonth other) => Ordinal.CompareTo(other.Ordinal);

    public static bool operator <(YearMonth a, YearMonth b) => a.Ordinal < b.Ordinal;
    public static bool operator >(YearMonth a, YearMonth b) => a.Ordinal > b.Ordinal;
    public static bool operator <=(YearMonth a, YearMonth b) => a.Ordinal <= b.Ordinal;
    public static bool operator >=(YearMonth a, YearMonth b) => a.Ordinal >= b.Ordinal;

    public static YearMonth Parse(string text)
    {
        if (TryParse(text, out var value)) return value;
        throw new FormatException($"'{text}' is not a valid month. Expected format yyyy-MM (e.g. 2025-04).");
    }

    public static bool TryParse(string? text, out YearMonth value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (DateTime.TryParseExact(text, new[] { "yyyy-MM", "yyyy-M", "yyyy/MM" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
        {
            value = new YearMonth(dt.Year, dt.Month);
            return true;
        }

        return false;
    }

    public static IEnumerable<YearMonth> Range(YearMonth first, YearMonth last)
    {
        for (var m = first; m <= last; m = m.AddMonths(1)) yield return m;
    }

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}

public sealed class YearMonthJsonConverter : JsonConverter<YearMonth>
{
    public override YearMonth Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        YearMonth.Parse(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, YearMonth value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
