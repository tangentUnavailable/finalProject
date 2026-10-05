using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Positions;

/// <summary>Shared position create/update logic for the JSON API and server-rendered components.</summary>
public static class PositionCommands
{
    public static async Task<(bool Ok, string? Error, int? Id)> CreateAsync(
        ApplicationDbContext db, string createdByUserId, PositionUpsert req, CancellationToken ct = default)
    {
        var pos = new Position
        {
            Title = req.Title, ShortDescription = req.ShortDescription, Company = req.Company,
            Level = req.Level, Access = req.Access, MaxProjects = Math.Clamp(req.MaxProjects <= 0 ? 5 : req.MaxProjects, 1, 20),
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Positions.Add(pos);
        await db.SaveChangesAsync(ct); // need position id first

        await ReplaceChildren(db, pos, req, ct);
        return (true, null, pos.Id);
    }

    public static async Task<(bool Ok, string? Error, uint? Version)> UpdateAsync(
        ApplicationDbContext db, int id, PositionUpsert req, CancellationToken ct = default)
    {
        var pos = await db.Positions.Include(p => p.Attributes).Include(p => p.Filters).Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pos is null) return (false, "Not found.", null);

        // Optimistic locking: validate version if provided
        if (req.Version is uint expected)
        {
            var current = db.Entry(pos).Property("xmin").CurrentValue as uint?;
            if (current is uint cur && cur != expected)
                return (false, "Position was modified by another user. Please refresh and try again.", null);
        }

        pos.Title = req.Title;
        pos.ShortDescription = req.ShortDescription;
        pos.Company = req.Company;
        pos.Level = req.Level;
        pos.Access = req.Access;
        pos.MaxProjects = Math.Clamp(req.MaxProjects <= 0 ? 5 : req.MaxProjects, 1, 20);
        pos.UpdatedAt = DateTimeOffset.UtcNow;

        await ReplaceChildren(db, pos, req, ct);
        return (true, null, db.Entry(pos).Property("xmin").CurrentValue as uint?);
    }

    public static async Task<List<TagRow>> AllTagsAsync(ApplicationDbContext db, CancellationToken ct = default) =>
        await db.Tags.OrderBy(t => t.Name).Select(t => new TagRow(t.Id, t.Name)).ToListAsync(ct);

    /// <summary>Duplicates a position with all its attributes, filters and tags. Returns the new id.</summary>
    public static async Task<int?> DuplicateAsync(ApplicationDbContext db, int id, string createdByUserId, CancellationToken ct = default)
    {
        var src = await db.Positions.Include(p => p.Attributes).Include(p => p.Filters).Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (src is null) return null;

        var copy = new Position
        {
            Title = src.Title + " (copy)", ShortDescription = src.ShortDescription, Company = src.Company,
            Level = src.Level, Access = src.Access, MaxProjects = src.MaxProjects, CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Positions.Add(copy);
        await db.SaveChangesAsync(ct);

        db.PositionAttributes.AddRange(src.Attributes.Select(a => new PositionAttribute
        { PositionId = copy.Id, AttributeId = a.AttributeId, Required = a.Required, SortOrder = a.SortOrder, Section = a.Section }));
        db.PositionFilters.AddRange(src.Filters.Select(f => new PositionFilter
        { PositionId = copy.Id, AttributeId = f.AttributeId, Operator = f.Operator, Value = f.Value }));
        db.PositionTags.AddRange(src.Tags.Select(t => new PositionTag { PositionId = copy.Id, TagId = t.TagId }));
        await db.SaveChangesAsync(ct);
        return copy.Id;
    }

    public static async Task<PosView?> LoadAsync(ApplicationDbContext db, int id, CancellationToken ct = default)
    {
        var p = await db.Positions.Where(x => x.Id == id)
            .Select(p => new
            {
                p.Id, p.Title, p.ShortDescription, p.Company, p.Level, p.Access, p.MaxProjects, p.UpdatedAt,
                Attributes = p.Attributes.OrderBy(a => a.SortOrder).Select(a => new
                {
                    a.Id, a.AttributeId, a.Required, a.SortOrder, a.Section,
                    Name = a.Attribute.Name, Type = a.Attribute.Type, Category = a.Attribute.Category,
                    Options = a.Attribute.Options,
                }),
                Filters = p.Filters.Select(f => new { f.Id, f.AttributeId, f.Operator, f.Value, Name = f.Attribute.Name, Type = f.Attribute.Type }),
                Tags = p.Tags.Select(t => new { t.TagId, t.Tag.Name }),
                Version = (uint?)db.Entry(p).Property("xmin").CurrentValue,
            })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;
        return new PosView(
            p.Id, p.Title, p.ShortDescription, p.Company, p.Level, p.Access, p.MaxProjects, p.UpdatedAt,
            p.Attributes.Select(a => new AttrDto(a.Id, a.AttributeId, a.Required, a.SortOrder, a.Section, a.Name, a.Type, a.Category, a.Options)).ToList(),
            p.Filters.Select(f => new FilterDto(f.Id, f.AttributeId, f.Operator, f.Value, f.Name, f.Type)).ToList(),
            p.Tags.Select(t => new TagDto(t.TagId, t.Name)).ToList(),
            p.Version);
    }

    private static async Task ReplaceChildren(ApplicationDbContext db, Position pos, PositionUpsert req, CancellationToken ct)
    {
        // Attributes: diff by (id, attributeId) to preserve rows when only flags change.
        var reqAttrKeys = req.Attributes.Select(a => (a.Id, a.AttributeId)).ToHashSet();
        db.PositionAttributes.RemoveRange(pos.Attributes.Where(a => !reqAttrKeys.Contains((a.Id, a.AttributeId))));

        var byId = pos.Attributes.ToDictionary(a => a.Id);
        var byAttr = pos.Attributes.Where(a => a.Id == 0 || !reqAttrKeys.Contains((a.Id, a.AttributeId)))
            .ToDictionary(a => a.AttributeId);
        var order = 0;
        foreach (var ra in req.Attributes)
        {
            if (ra.Id > 0 && byId.TryGetValue(ra.Id, out var existing))
            {
                existing.Required = ra.Required; existing.SortOrder = order; existing.Section = ra.Section;
            }
            else if (!byAttr.ContainsKey(ra.AttributeId))
            {
                db.PositionAttributes.Add(new PositionAttribute
                { PositionId = pos.Id, AttributeId = ra.AttributeId, Required = ra.Required, SortOrder = order, Section = ra.Section });
                byAttr[ra.AttributeId] = new PositionAttribute { AttributeId = ra.AttributeId };
            }
            order++;
        }

        // Filters: full replace (small sets).
        db.PositionFilters.RemoveRange(pos.Filters);
        db.PositionFilters.AddRange(req.Filters.Select(f => new PositionFilter
        { PositionId = pos.Id, AttributeId = f.AttributeId, Operator = f.Operator, Value = f.Value }));

        // Tags: full replace.
        db.PositionTags.RemoveRange(pos.Tags);
        foreach (var tagId in req.TagIds ?? [])
        {
            db.PositionTags.Add(new PositionTag { PositionId = pos.Id, TagId = tagId });
        }

        await db.SaveChangesAsync(ct);
    }

    public sealed record TagRow(int Id, string Name);
    public sealed record AttrDto(int Id, int AttributeId, bool Required, int SortOrder, string? Section, string Name, AttributeType Type, string Category, string? Options);
    public sealed record FilterDto(int Id, int AttributeId, FilterOperator Operator, string Value, string Name, AttributeType Type);
    public sealed record TagDto(int TagId, string Name);
    public sealed record PosView(int Id, string Title, string? ShortDescription, string? Company, string? Level, PositionAccess Access, int MaxProjects, DateTimeOffset UpdatedAt, List<AttrDto> Attributes, List<FilterDto> Filters, List<TagDto> Tags, uint? Version);
}
