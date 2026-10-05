using System.Text.RegularExpressions;
using CvHub.Domain;
using CvHub.Shared;

namespace CvHub.Infrastructure;

/// <summary>
/// Optional requirement #4: attribute "tuning" — enforces length limits, regex
/// validators and numeric ranges configured on an AttributeDef. Shared by the
/// profile autosave API and the in-place CV editor so the rules apply everywhere.
/// </summary>
public static class AttributeValueRules
{
    public static string? Validate(AttributeDef def, string? stringValue, string? textValue, double? numericValue)
    {
        switch (def.Type)
        {
            case AttributeType.String:
            case AttributeType.Text:
                var s = def.Type == AttributeType.String ? stringValue : textValue;
                if (s is not null)
                {
                    if (def.MinLength is int min && s.Length < min) return $"Value must be at least {min} characters.";
                    if (def.MaxLength is int max && s.Length > max) return $"Value must be at most {max} characters.";
                    if (!string.IsNullOrEmpty(def.RegexPattern) && !Regex.IsMatch(s, def.RegexPattern))
                        return "Value does not match the required format.";
                }
                break;
            case AttributeType.Numeric:
                if (numericValue is double n)
                {
                    if (def.MinValue is double lo && n < lo) return $"Value must be at least {lo}.";
                    if (def.MaxValue is double hi && n > hi) return $"Value must be at most {hi}.";
                }
                break;
        }
        return null;
    }
}
