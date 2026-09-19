using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Attributes;

/// <summary>Shared attribute create/update logic for the JSON API and server-rendered components.</summary>
public static class AttributeCommands
{
    public static async Task<(bool Ok, string? Error, int? Id)> CreateAsync(
        ApplicationDbContext db, AttrUpsert req, CancellationToken ct = default)
    {
        var name = req.Name.Trim();
        if (await db.Attributes.AnyAsync(a => a.Name == name, ct))
            return (false, $"Attribute '{name}' already exists.", null);

        var attr = new AttributeDef
        {
            Name = name, Category = req.Category, Description = req.Description,
            Type = req.Type, Options = req.Options, IsBuiltIn = false, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Attributes.Add(attr);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return (false, $"Attribute '{name}' already exists.", null); }
        return (true, null, attr.Id);
    }

    public static async Task<(bool Ok, string? Error, uint? Version)> UpdateAsync(
        ApplicationDbContext db, int id, AttrUpsert req, CancellationToken ct = default)
    {
        var attr = await db.Attributes.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (attr is null) return (false, "Not found.", null);
        if (attr.IsBuiltIn) return (false, "Built-in attributes cannot be modified.", null);

        // Optimistic locking: validate version if provided
        if (req.Version is uint expected)
        {
            var current = db.Entry(attr).Property("xmin").CurrentValue as uint?;
            if (current is uint cur && cur != expected)
                return (false, "Attribute was modified by another user. Please refresh and try again.", null);
        }

        var name = req.Name.Trim();
        if (await db.Attributes.AnyAsync(a => a.Name == name && a.Id != id, ct))
            return (false, $"Attribute '{name}' already exists.", null);

        attr.Name = name; attr.Category = req.Category; attr.Description = req.Description;
        attr.Type = req.Type; attr.Options = req.Options;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return (false, $"Attribute '{name}' already exists.", null); }
        return (true, null, db.Entry(attr).Property("xmin").CurrentValue as uint?);
    }
}
