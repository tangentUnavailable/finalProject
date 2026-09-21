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
            var rows = await db.Positions.AsNoTracking()
                .Select(p => new
                {
                    p.Id, p.Title, p.Company, p.Level, p.Access, p.UpdatedAt,
                    CvCount = p.Cvs.Count(c => c.Status == CvStatus.Published)
                })
                .ToListAsync();
            return Results.Ok(rows);
        });

        group.MapGet("/", async (ApplicationDbContext db) =>
            await db.Positions.AsNoTracking().OrderByDescending(p => p.UpdatedAt).Take(200)
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
            var (ok, error, id) = await PositionCommands.CreateAsync(db, uid, req);
            return ok ? Results.Ok(new { id }) : Results.Conflict(new { message = error });
        });

        group.MapPut("/{id:int}", async (int id, [FromBody] PositionUpsert req, ApplicationDbContext db) =>
        {
            var (ok, error, version) = await PositionCommands.UpdateAsync(db, id, req);
            if (!ok) return error == "Not found." ? Results.NotFound() : Results.Conflict(new { message = error });
            return Results.Ok(new { version });
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
            var newId = await PositionCommands.DuplicateAsync(db, id, uid);
            return newId is null ? Results.NotFound() : Results.Ok(new { id = newId });
        });
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
