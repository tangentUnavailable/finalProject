using CvHub.Domain;
using CvHub.Shared;

namespace CvHub.Infrastructure;

/// <summary>Evaluates position access rules against a user's attribute values.</summary>
public static class AccessRules
{
    public static bool HasAccess(
        Position position,
        Dictionary<int, AttributeValue> valuesByAttributeId,
        bool isAdminOrRecruiter)
    {
        if (isAdminOrRecruiter) return true;
        if (position.Access == PositionAccess.Public) return true;
        if (position.Filters.Count == 0) return true;

        return position.Filters.All(f => Matches(valuesByAttributeId.TryGetValue(f.AttributeId, out var v) ? v : null, f));
    }

    public static bool Matches(AttributeValue? value, PositionFilter filter)
    {
        var op = filter.Operator;
        switch (filter.Attribute.Type)
        {
            case AttributeType.Numeric:
            {
                if (!double.TryParse(filter.Value, System.Globalization.CultureInfo.InvariantCulture, out var target)) return false;
                return op switch
                {
                    FilterOperator.Equals => value?.NumericValue == target,
                    FilterOperator.NotEquals => value?.NumericValue != target,
                    FilterOperator.GreaterThan => value?.NumericValue > target,
                    FilterOperator.GreaterOrEqual => value?.NumericValue >= target,
                    FilterOperator.LessThan => value?.NumericValue < target,
                    FilterOperator.LessOrEqual => value?.NumericValue <= target,
                    _ => false,
                };
            }
            case AttributeType.Boolean:
            {
                var want = op == FilterOperator.IsTrue;
                return value is null ? !want : value.BoolValue == want;
            }
            case AttributeType.OneOfMany:
            {
                var v = value?.OptionValue ?? "";
                return op switch
                {
                    FilterOperator.Equals => v == filter.Value,
                    FilterOperator.NotEquals => v != filter.Value,
                    _ => false,
                };
            }
            default: // String/Text/Date/Period/Image
            {
                var v = value?.StringValue ?? value?.TextValue ?? value?.DateValue?.ToString("O") ?? "";
                return op switch
                {
                    FilterOperator.Equals => v == filter.Value,
                    FilterOperator.NotEquals => v != filter.Value,
                    _ => false,
                };
            }
        }
    }

    /// <summary>Operator choices for the filter editor, by attribute type.</summary>
    public static IEnumerable<FilterOperator> OperatorsFor(AttributeType type) => type switch
    {
        AttributeType.Numeric => [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.GreaterThan, FilterOperator.GreaterOrEqual, FilterOperator.LessThan, FilterOperator.LessOrEqual],
        AttributeType.Boolean => [FilterOperator.IsTrue, FilterOperator.IsFalse],
        AttributeType.OneOfMany => [FilterOperator.Equals, FilterOperator.NotEquals],
        _ => [FilterOperator.Equals, FilterOperator.NotEquals],
    };
}
