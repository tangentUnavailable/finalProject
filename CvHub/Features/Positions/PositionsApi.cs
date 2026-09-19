using System.Security.Claims;
using CvHub.Data;
using CvHub.Domain;
using CvHub.Infrastructure;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Positions;

public static class PositionsApi
{
    public static void MapPositionsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/positions").RequireAuthorization(p => p.RequireRole("Admin", "Recruiter")).DisableAntiforgery();

        group.MapGet("/list", async (ApplicationDbContext db) =>
        {
            var rows = await db.Positions
                .Select(p => new
                {
                    p.Id, p.Title, p.Company, p.Level, p.Access, p.UpdatedAt,
                    CvCount = p.Cvs.Count(c => c.Status == CvStatus.Published)
                })
                .ToListAsync();
            return Results.Ok(rows);
        });

        group.MapGet("/", async (ApplicationDbContext db) =>
            await db.Positions.OrderByDescending(p => p.UpdatedAt).Take(200)
                .Select(p => new { p.Id, p.Title, p.Company, p.Level, p.Access, p.UpdatedAt })
                .ToListAsync());

        group.MapGet("/{id:int}", async (int id, ApplicationDbContext db) =>
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
                .FirstOrDefaultAsync();
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        group.MapPost("/", async ([FromBody] PositionUpsert req, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User)!;
            var pos = new Position
            {
                Title = req.Title, ShortDescription = req.ShortDescription, Company = req.Company,
                Level = req.Level, Access = req.Access, MaxProjects = Math.Clamp(req.MaxProjects <= 0 ? 5 : req.MaxProjects, 1, 20),
                CreatedByUserId = uid,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Positions.Add(pos);
            await db.SaveChangesAsync(); // need position id first

            await ReplaceChildren(db, pos, req);
            return Results.Ok(new { id = pos.Id });
        });

        group.MapPut("/{id:int}", async (int id, [FromBody] PositionUpsert req, ApplicationDbContext db) =>
        {
            var pos = await db.Positions.Include(p => p.Attributes).Include(p => p.Filters).Include(p => p.Tags)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (pos is null) return Results.NotFound();

            // Optimistic locking: validate version if provided
            if (req.Version is uint expected)
            {
                var current = db.Entry(pos).Property("xmin").CurrentValue as uint?;
                if (current is uint cur && cur != expected)
                    return Results.Conflict(new { message = "Position was modified by another user. Please refresh and try again." });
            }

            pos.Title = req.Title;
            pos.ShortDescription = req.ShortDescription;
            pos.Company = req.Company;
            pos.Level = req.Level;
            pos.Access = req.Access;
            pos.MaxProjects = Math.Clamp(req.MaxProjects <= 0 ? 5 : req.MaxProjects, 1, 20);
            pos.UpdatedAt = DateTimeOffset.UtcNow;

            await ReplaceChildren(db, pos, req);
            return Results.Ok(new { version = db.Entry(pos).Property("xmin").CurrentValue });
        });

        group.MapDelete("/{id:int}", async (int id, ApplicationDbContext db) =>
        {
            var pos = await db.Positions.FirstOrDefaultAsync(p => p.Id == id);
            if (pos is null) return Results.NotFound();
            pos.IsDeleted = true;
            pos.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPost("/{id:int}/duplicate", async (int id, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User)!;
            var src = await db.Positions.Include(p => p.Attributes).Include(p => p.Filters).Include(p => p.Tags)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (src is null) return Results.NotFound();

            var copy = new Position
            {
                Title = src.Title + " (copy)", ShortDescription = src.ShortDescription, Company = src.Company,
                Level = src.Level, Access = src.Access, MaxProjects = src.MaxProjects, CreatedByUserId = uid,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Positions.Add(copy);
            await db.SaveChangesAsync();

            db.PositionAttributes.AddRange(src.Attributes.Select(a => new PositionAttribute
            { PositionId = copy.Id, AttributeId = a.AttributeId, Required = a.Required, SortOrder = a.SortOrder, Section = a.Section }));
            db.PositionFilters.AddRange(src.Filters.Select(f => new PositionFilter
            { PositionId = copy.Id, AttributeId = f.AttributeId, Operator = f.Operator, Value = f.Value }));
            db.PositionTags.AddRange(src.Tags.Select(t => new PositionTag { PositionId = copy.Id, TagId = t.TagId }));
            await db.SaveChangesAsync();
            return Results.Ok(new { id = copy.Id });
        });
    }

    private static async Task ReplaceChildren(ApplicationDbContext db, Position pos, PositionUpsert req)
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

        await db.SaveChangesAsync();
    }
}

public record PositionUpsert(
    string Title, string? ShortDescription, string? Company, string? Level,
    PositionAccess Access, int MaxProjects,
    List<PositionAttributeReq> Attributes,
    List<PositionFilterReq> Filters,
    List<int>? TagIds,
    uint? Version);

public record PositionAttributeReq(int Id, int AttributeId, bool Required, string? Section);
public record PositionFilterReq(int AttributeId, FilterOperator Operator, string Value);
