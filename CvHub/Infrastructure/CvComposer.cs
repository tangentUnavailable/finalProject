using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Infrastructure;

/// <summary>Composes the live CV view model from profile + position template. Nothing is copied.</summary>
public static class CvComposer
{
    public static async Task<CvViewDto?> ComposeAsync(ApplicationDbContext db, Cv cv, CancellationToken ct = default)
    {
        var position = await db.Positions.Include(p => p.Attributes).ThenInclude(pa => pa.Attribute)
            .FirstOrDefaultAsync(p => p.Id == cv.PositionId, ct);
        if (position is null) return null;

        var attrIds = position.Attributes.Select(a => a.AttributeId).ToList();
        var values = await db.AttributeValues
            .Where(v => v.UserId == cv.UserId && attrIds.Contains(v.AttributeId))
            .ToDictionaryAsync(v => v.AttributeId, ct);

        // Single query: projects belonging to the CV's owner that are linked to this CV
        // (replaces the two-step projectIds + Projects load).
        var projects = await db.Projects.Include(p => p.Tags).ThenInclude(pt => pt.Tag)
            .Where(p => p.UserId == cv.UserId && db.CvProjects.Any(cp => cp.CvId == cv.Id && cp.ProjectId == p.Id))
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        var fields = position.Attributes.OrderBy(a => a.SortOrder).Select(pa =>
        {
            values.TryGetValue(pa.AttributeId, out var v);
            return ToField(pa, v, Xmin(db, v));
        }).ToList();

        var missing = fields.Count(f => f.Required && f.IsEmpty);

        return new CvViewDto(
            cv.Id, cv.PositionId, position.Title, cv.UserId, cv.Status,
            fields, projects.Select(p => new ProjectDto(p.Id, p.Name, p.PeriodStart, p.PeriodEnd, p.Description,
                p.Tags.Select(t => t.Tag.Name).ToArray())).ToArray(),
            missing);
    }

    public static CvFieldDto ToField(PositionAttribute pa, AttributeValue? v, uint? version = null) => new(
        pa.AttributeId,
        pa.Attribute.Name,
        pa.Attribute.Type,
        pa.Attribute.Category,
        pa.Required,
        pa.Section,
        v is null || IsEmpty(v, pa.Attribute.Type),
        v?.StringValue, v?.TextValue, v?.ImageUrl, v?.NumericValue,
        v?.DateValue, v?.PeriodStart, v?.PeriodEnd,
        v?.BoolValue ?? false, v?.OptionValue,
        (pa.Attribute.Options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        version);

    /// <summary>Reads the xmin row version of a tracked entity (null when unsaved).</summary>
    public static uint? Xmin(ApplicationDbContext db, AttributeValue? v) =>
        v is null ? null : db.Entry(v).Property("xmin").CurrentValue as uint?;

    public static bool IsEmpty(AttributeValue v, AttributeType t) => t switch
    {
        AttributeType.String => string.IsNullOrWhiteSpace(v.StringValue),
        AttributeType.Text => string.IsNullOrWhiteSpace(v.TextValue),
        AttributeType.Image => string.IsNullOrWhiteSpace(v.ImageUrl),
        AttributeType.Numeric => v.NumericValue is null,
        AttributeType.Date => v.DateValue is null,
        AttributeType.Period => v.PeriodStart is null && v.PeriodEnd is null,
        AttributeType.Boolean => false,
        AttributeType.OneOfMany => string.IsNullOrWhiteSpace(v.OptionValue),
        _ => true,
    };

    /// <summary>Attaches or removes a profile attribute when a CV field is edited in place.</summary>
    public static async Task EnsureProfileAttributeAsync(ApplicationDbContext db, string userId, int attributeId, CancellationToken ct = default)
    {
        var exists = await db.ProfileAttributes.AnyAsync(x => x.UserId == userId && x.AttributeId == attributeId, ct);
        if (!exists)
        {
            db.ProfileAttributes.Add(new ProfileAttribute { UserId = userId, AttributeId = attributeId });
            await db.SaveChangesAsync(ct);
        }
    }
}
