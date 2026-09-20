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

    /// <summary>Escape a value for a single CSV cell (RFC 4180 quoting).</summary>
    public static string CsvCell(string? value)
    {
        if (value is null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
