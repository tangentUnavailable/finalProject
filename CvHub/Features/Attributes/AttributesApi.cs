using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Attributes;

public static class AttributesApi
{
    public static void MapAttributesApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/attributes").RequireAuthorization(p => p.RequireRole("Admin", "Recruiter")).DisableAntiforgery();

        group.MapGet("/lookup", async (string? prefix, string? category, string? ids, ApplicationDbContext db) =>
        {
            var q = db.Attributes.AsQueryable();
            if (!string.IsNullOrEmpty(ids))
            {
                var idList = ids.Split(',').Select(int.Parse).ToList();
                return Results.Ok(await q.Where(a => idList.Contains(a.Id))
                    .Select(a => new { a.Id, a.Name, a.Category, a.Type, Version = (uint?)db.Entry(a).Property("xmin").CurrentValue }).ToListAsync());
            }
            if (!string.IsNullOrEmpty(prefix))
                q = q.Where(a => a.Name.ToLower().StartsWith(prefix.ToLower()));
            if (!string.IsNullOrEmpty(category))
                q = q.Where(a => a.Category == category);
            var rows = await q.OrderBy(a => a.Name).Take(20)
                .Select(a => new { a.Id, a.Name, a.Category, a.Type, Version = (uint?)db.Entry(a).Property("xmin").CurrentValue }).ToListAsync();
            return Results.Ok(rows);
        });

        group.MapGet("/recent", async (ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Ok(Array.Empty<object>());
            var rows = await db.RecentAttributes.Where(r => r.UserId == uid)
                .OrderByDescending(r => r.UsedAt).Take(5)
                .Join(db.Attributes, r => r.AttributeId, a => a.Id, (r, a) => new { a.Id, a.Name, a.Category, a.Type })
                .ToListAsync();
            return Results.Ok(rows);
        });

        group.MapPost("/recent/{attrId:int}", async (int attrId, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Ok();
            var existing = await db.RecentAttributes.FirstOrDefaultAsync(r => r.UserId == uid && r.AttributeId == attrId);
            if (existing is not null) existing.UsedAt = DateTimeOffset.UtcNow;
            else db.RecentAttributes.Add(new RecentAttribute { UserId = uid, AttributeId = attrId, UsedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPost("/", async ([FromBody] AttrUpsert req, ApplicationDbContext db) =>
        {
            var name = req.Name.Trim();
            if (await db.Attributes.AnyAsync(a => a.Name == name))
                return Results.Conflict(new { message = $"Attribute '{name}' already exists." });

            var attr = new AttributeDef
            {
                Name = name, Category = req.Category, Description = req.Description,
                Type = req.Type, Options = req.Options, IsBuiltIn = false, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Attributes.Add(attr);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { message = $"Attribute '{name}' already exists." }); }
            return Results.Ok(new { id = attr.Id });
        });

        group.MapPut("/{id:int}", async (int id, [FromBody] AttrUpsert req, ApplicationDbContext db) =>
        {
            var attr = await db.Attributes.FirstOrDefaultAsync(a => a.Id == id);
            if (attr is null) return Results.NotFound();
            if (attr.IsBuiltIn) return Results.Conflict(new { message = "Built-in attributes cannot be modified." });

            // Optimistic locking: validate version if provided
            if (req.Version is uint expected)
            {
                var current = db.Entry(attr).Property("xmin").CurrentValue as uint?;
                if (current is uint cur && cur != expected)
                    return Results.Conflict(new { message = "Attribute was modified by another user. Please refresh and try again." });
            }

            var name = req.Name.Trim();
            if (await db.Attributes.AnyAsync(a => a.Name == name && a.Id != id))
                return Results.Conflict(new { message = $"Attribute '{name}' already exists." });

            attr.Name = name; attr.Category = req.Category; attr.Description = req.Description;
            attr.Type = req.Type; attr.Options = req.Options;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { message = $"Attribute '{name}' already exists." }); }
            return Results.Ok(new { version = db.Entry(attr).Property("xmin").CurrentValue });
        });

        group.MapDelete("/{id:int}", async (int id, ApplicationDbContext db) =>
        {
            var attr = await db.Attributes.Include(a => a.PositionAttributes).FirstOrDefaultAsync(a => a.Id == id);
            if (attr is null) return Results.NotFound();
            if (attr.IsBuiltIn) return Results.Conflict(new { message = "Built-in attributes cannot be deleted." });
            if (attr.PositionAttributes.Count > 0)
                return Results.Conflict(new { message = "Attribute is used by positions — remove it from those templates first." });
            attr.IsDeleted = true;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}

public record AttrUpsert(string Name, string Category, string? Description, AttributeType Type, string? Options, uint? Version);
