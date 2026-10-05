namespace CvHub.Shared;

// ---------- Cross-cutting enums / DTOs shared by server pages and WASM client components ----------

/// <summary>Attribute-based access rule operator (lives in Shared so the WASM client can use it).</summary>
public enum FilterOperator
{
    Equals = 1,
    NotEquals = 2,
    GreaterThan = 3,
    GreaterOrEqual = 4,
    LessThan = 5,
    LessOrEqual = 6,
    IsTrue = 7,
    IsFalse = 8,
}

/// <summary>DTO for one attribute row in a generated CV.</summary>
public record CvFieldDto(
    int AttributeId,
    string Name,
    AttributeType Type,
    string Category,
    bool Required,
    string? Section,
    bool IsEmpty,
    string? StringValue,
    string? TextValue,
    string? ImageUrl,
    double? NumericValue,
    DateOnly? DateValue,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    bool BoolValue,
    string? OptionValue,
    string[] Options,
    uint? Version = null);

/// <summary>Project row shared by profile / CV / public profile views.</summary>
public record ProjectDto(int Id, string Name, DateOnly? Start, DateOnly? End, string Description, string[] Tags);

/// <summary>Composed CV view model (content looked up live, never copied).</summary>
public record CvViewDto(
    int CvId,
    int PositionId,
    string PositionTitle,
    string UserId,
    CvStatus Status,
    IReadOnlyList<CvFieldDto> Fields,
    IReadOnlyList<ProjectDto> Projects,
    int MissingRequired);
