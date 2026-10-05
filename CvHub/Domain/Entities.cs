using System.ComponentModel.DataAnnotations;
using CvHub.Shared;

namespace CvHub.Domain;

public abstract class Entity
{
    public int Id { get; set; }
}

/// <summary>Attribute definition in the shared library. Globally unique name.</summary>
public class AttributeDef : Entity
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(80)]
    public string Category { get; set; } = "Other";

    [MaxLength(400)]
    public string? Description { get; set; }

    public AttributeType Type { get; set; }

    /// <summary>Dropdown options for OneOfMany attributes, newline-separated.</summary>
    public string? Options { get; set; }

    // Optional "tuning" (optional requirement #4): enforced when a candidate fills a value.
    //   String / Text  -> MinLength / MaxLength / RegexPattern
    //   Numeric        -> MinValue / MaxValue
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? RegexPattern { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }

    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public ICollection<PositionAttribute> PositionAttributes { get; set; } = [];
    public ICollection<AttributeValue> Values { get; set; } = [];
    public ICollection<ProfileAttribute> ProfileAttributes { get; set; } = [];
}

/// <summary>Single master value of an attribute for one user. Source of truth for profile and CVs.</summary>
public class AttributeValue : Entity
{
    public string UserId { get; set; } = "";
    public int AttributeId { get; set; }
    public AttributeDef Attribute { get; set; } = null!;

    // Per-type value columns (only one is set per attribute type).
    public string? StringValue { get; set; }
    public string? TextValue { get; set; }
    public string? ImageUrl { get; set; }
    public double? NumericValue { get; set; }
    public DateOnly? DateValue { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public bool BoolValue { get; set; }
    public string? OptionValue { get; set; }
}

/// <summary>Pinned library attributes shown in the Info section of the profile.</summary>
public class ProfileAttribute : Entity
{
    public string UserId { get; set; } = "";
    public int AttributeId { get; set; }
    public AttributeDef Attribute { get; set; } = null!;
    public int SortOrder { get; set; }
}

public class Project : Entity
{
    [Required, MaxLength(160)]
    public string Name { get; set; } = "";

    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }

    [Required]
    public string Description { get; set; } = "";

    public string UserId { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ProjectTag> Tags { get; set; } = [];
}

public class Tag : Entity
{
    [Required, MaxLength(60)]
    public string Name { get; set; } = "";
    public ICollection<ProjectTag> Projects { get; set; } = [];
    public ICollection<PositionTag> Positions { get; set; } = [];
}

public class ProjectTag : Entity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

public class Position : Entity
{
    [Required, MaxLength(160)]
    public string Title { get; set; } = "";

    [MaxLength(600)]
    public string? ShortDescription { get; set; }

    [MaxLength(120)]
    public string? Company { get; set; }

    [MaxLength(40)]
    public string? Level { get; set; }  // Junior / Middle / Senior / C-level / ...

    public PositionAccess Access { get; set; } = PositionAccess.Public;

    /// <summary>Maximum number of candidate projects included in a generated CV.</summary>
    public int MaxProjects { get; set; } = 5;

    public string CreatedByUserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Store-generated tsvector column (created by raw SQL in the initial migration) backing the GIN full-text index.</summary>
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }

    public ICollection<PositionAttribute> Attributes { get; set; } = [];
    public ICollection<PositionFilter> Filters { get; set; } = [];
    public ICollection<PositionTag> Tags { get; set; } = [];
    public ICollection<Cv> Cvs { get; set; } = [];
}

/// <summary>Attribute required by a position, with per-position presentation settings.</summary>
public class PositionAttribute : Entity
{
    public int PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public int AttributeId { get; set; }
    public AttributeDef Attribute { get; set; } = null!;
    public bool Required { get; set; }
    public int SortOrder { get; set; }
    public string? Section { get; set; }  // CV section heading, e.g. "Skills"
}

/// <summary>Attribute-based access rule: &lt;Attribute&gt; &lt;Operator&gt; &lt;Value&gt;.</summary>
public class PositionFilter : Entity
{
    public int PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public int AttributeId { get; set; }
    public AttributeDef Attribute { get; set; } = null!;
    public FilterOperator Operator { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>Project tag a position asks for (relevant projects for the generated CV).</summary>
public class PositionTag : Entity
{
    public int PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

/// <summary>A CV "instance": content is looked up from AttributeValues at render time, never copied.</summary>
public class Cv : Entity
{
    public int PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public string UserId { get; set; } = "";
    public CvStatus Status { get; set; } = CvStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public bool IsDeleted { get; set; }
    public ICollection<CvProject> Projects { get; set; } = [];
}

/// <summary>Which of the candidate's projects are included in a given CV.</summary>
public class CvProject : Entity
{
    public int CvId { get; set; }
    public Cv Cv { get; set; } = null!;
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
}

public class Comment : Entity
{
    public int PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public string UserId { get; set; } = "";
    [Required]
    public string Body { get; set; } = "";  // Markdown
    public DateTimeOffset CreatedAt { get; set; }
}

public class CvLike : Entity
{
    public int CvId { get; set; }
    public Cv Cv { get; set; } = null!;
    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Per-user "recently used" attribute ids for the attribute picker.</summary>
public class RecentAttribute : Entity
{
    public string UserId { get; set; } = "";
    public int AttributeId { get; set; }
    public DateTimeOffset UsedAt { get; set; }
}

/// <summary>Version tracking for attribute definitions within position templates (cv_attribute_versions).</summary>
public class CvAttributeVersion : Entity
{
    public int PositionId { get; set; }
    public Position Position { get; set; } = null!;
    public int AttributeId { get; set; }
    public AttributeDef Attribute { get; set; } = null!;
    public uint Version { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>
/// CRM link for one site user: the Salesforce Account + Contact created from their profile.
/// One row per user — a re-sync updates the same Salesforce records instead of duplicating them.
/// </summary>
public class CrmLink : Entity
{
    [Required]
    public string UserId { get; set; } = "";

    /// <summary>18-char Salesforce record Id of the Account.</summary>
    [MaxLength(32)]
    public string SfAccountId { get; set; } = "";

    /// <summary>18-char Salesforce record Id of the linked Contact.</summary>
    [MaxLength(32)]
    public string SfContactId { get; set; } = "";

    /// <summary>Instance URL returned by the OAuth token response (per-org, e.g. https://org.my.salesforce.com).</summary>
    [MaxLength(256)]
    public string InstanceUrl { get; set; } = "";

    /// <summary>Salesforce refresh token (web-server flow); lets later requests get fresh access tokens.</summary>
    [MaxLength(512)]
    public string? RefreshToken { get; set; }

    /// <summary>Newsletter opt-in captured in the sync form.</summary>
    public bool NewsletterOptIn { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>
/// API token for external integrations (e.g. the Odoo connector). One token per
/// "inventory": it is minted on a position page and grants access ONLY to that
/// position's aggregated results, plus (for the optional export-back) position
/// creation. A token therefore never exposes another position's candidates.
/// </summary>
public class ExternalApiToken : Entity
{
    [Required, MaxLength(64)]
    public string Token { get; set; } = "";

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    /// <summary>The position this token is scoped to. Null only for tokens created before scoping existed.</summary>
    public int? PositionId { get; set; }

    public string CreatedByUserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
