using System.Security.Claims;
using CvHub.Data;
using CvHub.Domain;
using CvHub.Infrastructure;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Profile;

/// <summary>JSON API backing the profile page: autosave, attribute pinning, projects.</summary>
public static class ProfileApi
{
    public static void MapProfileApi(this IEndpointRouteBuilder app)
    {
        var profileGroup = app.MapGroup("").DisableAntiforgery();

        // ---------- Save one attribute value (optimistic locking via xmin) ----------
        profileGroup.MapPost("/api/profile/attributes/{attrId:int}", async (
            int attrId, [FromBody] SaveValueRequest req, HttpContext http,
            ApplicationDbContext db, UserManager<ApplicationUser> users) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            if (http.User.IsInRole("Recruiter") && !http.User.IsInRole("Admin") && uid != req.UserId)
                return Results.Json(new { message = "Recruiters cannot edit candidate profiles." }, statusCode: 403);

            var targetUser = req.UserId ?? uid;
            var admin = http.User.IsInRole("Admin");
            if (targetUser != uid && !admin)
                return Results.Json(new { message = "Only admins may edit other profiles." }, statusCode: 403);

            var def = await db.Attributes.FirstOrDefaultAsync(a => a.Id == attrId);
            if (def is null) return Results.NotFound();

            var value = await db.AttributeValues.FirstOrDefaultAsync(v => v.UserId == targetUser && v.AttributeId == attrId);
            var isNew = value is null;

            // Optimistic locking: client echoes the version it read; mismatch -> 409.
            if (!isNew && req.Version is uint expected)
            {
                var current = db.Entry(value!).Property("xmin").CurrentValue;
                if (current is uint cur && cur != expected)
                    return Results.Json(new { message = "Changed elsewhere.", version = cur }, statusCode: 409);
            }

            if (isNew)
            {
                value = new AttributeValue { UserId = targetUser, AttributeId = attrId };
                db.AttributeValues.Add(value);
            }

            ApplyValue(value!, def.Type, req);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Json(new { message = "Changed elsewhere." }, statusCode: 409);
            }

            // Built-in attributes are auto-pinned to the profile on first save.
            if (isNew && def.IsBuiltIn &&
                !await db.ProfileAttributes.AnyAsync(pa => pa.UserId == targetUser && pa.AttributeId == attrId))
            {
                db.ProfileAttributes.Add(new ProfileAttribute { UserId = targetUser, AttributeId = attrId });
                await db.SaveChangesAsync();
            }

            return Results.Ok(new { version = db.Entry(value!).Property("xmin").CurrentValue, created = isNew });
        });

        // ---------- Read one attribute value with its optimistic-lock version ----------
        profileGroup.MapGet("/api/profile/attributes/{attrId:int}", async (
            int attrId, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var value = await db.AttributeValues.FirstOrDefaultAsync(v => v.UserId == uid && v.AttributeId == attrId);
            return Results.Ok(new
            {
                version = value is null ? null : (uint?)db.Entry(value).Property("xmin").CurrentValue!,
                value?.StringValue, value?.TextValue, value?.ImageUrl,
                value?.NumericValue, value?.DateValue, value?.PeriodStart, value?.PeriodEnd,
                value?.BoolValue, value?.OptionValue,
            });
        });

        // ---------- Pin / unpin attribute from profile Info section ----------
        profileGroup.MapPost("/api/profile/attributes/{attrId:int}/pin", async (
            int attrId, [FromBody] PinRequest req, HttpContext http,
            ApplicationDbContext db, UserManager<ApplicationUser> users) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var targetUser = req.UserId ?? uid;
            if (targetUser != uid && !http.User.IsInRole("Admin")) return Results.Forbid();

            var pin = await db.ProfileAttributes.FirstOrDefaultAsync(pa => pa.UserId == targetUser && pa.AttributeId == attrId);
            if (req.Pinned && pin is null)
            {
                var max = await db.ProfileAttributes.Where(pa => pa.UserId == targetUser).MaxAsync(pa => (int?)pa.SortOrder) ?? 0;
                db.ProfileAttributes.Add(new ProfileAttribute { UserId = targetUser, AttributeId = attrId, SortOrder = max + 1 });
            }
            else if (!req.Pinned && pin is not null)
            {
                db.ProfileAttributes.Remove(pin);
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Projects ----------
        profileGroup.MapPost("/api/profile/projects", async (
            [FromBody] ProjectRequest req, HttpContext http,
            ApplicationDbContext db, UserManager<ApplicationUser> users) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var targetUser = req.UserId ?? uid;
            if (targetUser != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Only admins may edit other profiles." }, statusCode: 403);

            var project = req.Id is int id && id > 0
                ? await db.Projects.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == id)
                : null;
            if (project is null)
            {
                project = new Project { UserId = targetUser, CreatedAt = DateTimeOffset.UtcNow };
                var max = await db.Projects.Where(p => p.UserId == targetUser).MaxAsync(p => (int?)p.SortOrder) ?? 0;
                project.SortOrder = max + 1;
                db.Projects.Add(project);
            }

            project.Name = req.Name;
            project.PeriodStart = req.Start;
            project.PeriodEnd = req.End;
            project.Description = req.Description ?? "";

            var tagNames = (req.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = await db.Tags.Where(t => tagNames.Contains(t.Name)).ToDictionaryAsync(t => t.Name);
            db.ProjectTags.RemoveRange(project.Tags);
            foreach (var name in tagNames)
            {
                if (!existing.TryGetValue(name, out var tag))
                {
                    tag = new Tag { Name = name };
                    db.Tags.Add(tag);
                    existing[name] = tag;
                }
                db.ProjectTags.Add(new ProjectTag { Project = project, Tag = tag });
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { id = project.Id });
        });

        profileGroup.MapDelete("/api/profile/projects/{id:int}", async (
            int id, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var project = await db.Projects.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();
            if (project.UserId != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Not your project." }, statusCode: 403);

            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Tags: full list (position editor) + autocomplete ----------
        profileGroup.MapGet("/api/tags", async (ApplicationDbContext db) =>
            await db.Tags.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync());

        profileGroup.MapGet("/api/tags/suggest", async (string? prefix, ApplicationDbContext db) =>
            await db.Tags
                .Where(t => prefix == null || EF.Functions.ILike(t.Name, prefix + "%"))
                .OrderBy(t => t.Name)
                .Take(10)
                .Select(t => t.Name)
                .ToArrayAsync());

        // ---------- Pinned attributes for attribute picker ----------
        profileGroup.MapGet("/api/profile/attributes/pinned", async (
            ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Ok(Array.Empty<int>());
            var pinned = await db.ProfileAttributes
                .Where(pa => pa.UserId == uid)
                .Select(pa => pa.AttributeId)
                .ToListAsync();
            return Results.Ok(pinned);
        });
    }

    private static void ApplyValue(AttributeValue value, AttributeType type, SaveValueRequest req)
    {
        switch (type)
        {
            case AttributeType.String: value.StringValue = req.StringValue; break;
            case AttributeType.Text: value.TextValue = req.TextValue; break;
            case AttributeType.Image: value.ImageUrl = req.ImageUrl; break;
            case AttributeType.Numeric: value.NumericValue = req.NumericValue; break;
            case AttributeType.Date: value.DateValue = req.DateValue; break;
            case AttributeType.Period: value.PeriodStart = req.PeriodStart; value.PeriodEnd = req.PeriodEnd; break;
            case AttributeType.Boolean: value.BoolValue = req.BoolValue; break;
            case AttributeType.OneOfMany: value.OptionValue = req.OptionValue; break;
        }
    }
}

public record SaveValueRequest(
    string? UserId,
    uint? Version,
    string? StringValue, string? TextValue, string? ImageUrl,
    double? NumericValue, DateOnly? DateValue, DateOnly? PeriodStart, DateOnly? PeriodEnd,
    bool BoolValue, string? OptionValue);

public record PinRequest(string? UserId, bool Pinned);

public record ProjectRequest(
    int? Id, string? UserId, string Name, DateOnly? Start, DateOnly? End,
    string? Description, string[]? Tags);
