using System.Globalization;
using CvHub.Shared;

namespace CvHub.Infrastructure;

/// <summary>Shared value formatting for non-Razor rendering (CSV export, PDF generator).</summary>
public static class CvDisplay
{
    public static string FieldValue(CvFieldDto f)
    {
        if (f.IsEmpty) return "(empty)";
        return f.Type switch
        {
            AttributeType.Boolean => f.BoolValue ? "Yes" : "No",
            AttributeType.Numeric => f.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            AttributeType.Date => f.DateValue?.ToString("yyyy-MM-dd") ?? "",
            AttributeType.Period => $"{(f.PeriodStart is null ? "—" : f.PeriodStart?.ToString("yyyy-MM-dd"))} – {(f.PeriodEnd is null ? "present" : f.PeriodEnd?.ToString("yyyy-MM-dd"))}",
            AttributeType.Image => f.ImageUrl ?? "",
            AttributeType.OneOfMany => f.OptionValue ?? "",
            _ => f.StringValue ?? f.TextValue ?? "",
        };
    }

    /// <summary>Escape a value for a single CSV cell (RFC 4180 quoting + Excel formula-injection guard).</summary>
    public static string CsvCell(string? value)
    {
        if (value is null) return "";
        // Cells starting with these can execute as spreadsheet formulas when the CSV is opened;
        // leading '-' is only dangerous when not a plain number (negative numerics stay intact).
        var dangerous = value[0] is '=' or '+' or '@' or '\t'
            || (value[0] == '-' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        var guarded = value.Length > 0 && dangerous ? "'" + value : value;
        if (guarded.Contains(',') || guarded.Contains('"') || guarded.Contains('\n') || guarded.Contains('\r'))
            return "\"" + guarded.Replace("\"", "\"\"") + "\"";
        return guarded;
    }
}
