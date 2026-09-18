namespace CvHub.Shared;

/// <summary>Data types supported by the Attribute Library.</summary>
public enum AttributeType
{
    String = 1,
    Text = 2,
    Image = 3,
    Numeric = 4,
    Date = 5,
    Period = 6,
    Boolean = 7,
    OneOfMany = 8,
}

/// <summary>Publishing lifecycle of a CV.</summary>
public enum CvStatus
{
    Draft = 1,
    Published = 2,
}

/// <summary>Access mode of a position.</summary>
public enum PositionAccess
{
    /// <summary>Accessible to every authenticated user.</summary>
    Public = 1,
    /// <summary>Restricted by attribute-based filter rules.</summary>
    Restricted = 2,
}

/// <summary>Predefined categories for the Attribute Library.</summary>
public static class AttributeCategories
{
    public static readonly string[] All =
    [
        "Certification", "Domain Knowledge", "Personal Information",
        "Soft Skills", "Education", "Experience", "Languages", "Other",
    ];
}

/// <summary>Names of the built-in "Me" profile attributes. Seeded once, cannot be removed.</summary>
public static class BuiltInAttributeNames
{
    public const string FirstName = "First Name";
    public const string LastName = "Last Name";
    public const string Location = "Location";
    public const string Photo = "Personal Photo";
    public const string Headline = "Headline";
    public const string About = "About Me";

    public static readonly string[] All = [FirstName, LastName, Location, Photo, Headline, About];
}
