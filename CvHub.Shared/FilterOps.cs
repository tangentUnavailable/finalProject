namespace CvHub.Shared;

/// <summary>Client-side operator metadata for the position filter editor.</summary>
public static class FilterOps
{
    public static IEnumerable<FilterOperator> OperatorsFor(AttributeType type) => type switch
    {
        AttributeType.Numeric => [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.GreaterThan, FilterOperator.GreaterOrEqual, FilterOperator.LessThan, FilterOperator.LessOrEqual],
        AttributeType.Boolean => [FilterOperator.IsTrue, FilterOperator.IsFalse],
        AttributeType.OneOfMany => [FilterOperator.Equals, FilterOperator.NotEquals],
        _ => [FilterOperator.Equals, FilterOperator.NotEquals],
    };

    public static string Name(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.NotEquals => "≠",
        FilterOperator.GreaterThan => ">",
        FilterOperator.GreaterOrEqual => "≥",
        FilterOperator.LessThan => "<",
        FilterOperator.LessOrEqual => "≤",
        FilterOperator.IsTrue => "is true",
        FilterOperator.IsFalse => "is false",
        _ => op.ToString(),
    };
}
