using System.Security.Cryptography;
using System.Text;
using CvHub.Data;
using CvHub.Domain;
using CvHub.Features.Positions;
using CvHub.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.External;

/// <summary>
/// Externally accessible API for external consumers (e.g. the Odoo connector).
/// Access is granted via an API token generated per "inventory" (one external
/// consumer); the token gates everything under /api/ext. Read endpoints expose
/// aggregated results from the positions (published CVs only); the optional
/// export-back endpoint creates positions in this app from an external system.
/// </summary>
public static class ExternalApi
{
    public static void MapExternalApi(this IEndpointRouteBuilder app)
    {
        // ---------- Token management (recruiters/admins, cookie auth) ----------
        var admin = app.MapGroup("/api/ext/tokens")
            .RequireAuthorization(p => p.RequireRole("Admin", "Recruiter"));

        admin.MapPost("/", async ([FromBody] CreateTokenRequest req, ApplicationDbContext db, HttpContext http) =>
        {
            var uid = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
            var entity = await CreateTokenAsync(db, req.Name, uid);
            // The plaintext token is returned exactly once, here.
            return Results.Ok(new { entity.Id, entity.Name, token = entity.Token, entity.CreatedAt });
        });

        admin.MapGet("/", async (ApplicationDbContext db) =>
            Results.Ok(await db.ExternalApiTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt)
                .Select(t => new { t.Id, t.Name, t.CreatedAt, t.RevokedAt, t.LastUsedAt, prefix = t.Token.Substring(0, Math.Min(8, t.Token.Length)) + "…" })
                .ToListAsync()));

        admin.MapPost("/{id:int}/revoke", async (int id, ApplicationDbContext db) =>
        {
            var token = await db.ExternalApiTokens.FirstOrDefaultAsync(t => t.Id == id);
            if (token is null) return Results.NotFound();
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Integration endpoints (X-Api-Token header or ?api_token=) ----------
        var ext = app.MapGroup("/api/ext");

        // Position list for the token's inventory.
        ext.MapGet("/positions", async (ApplicationDbContext db, [FromQuery] string? api_token, HttpContext http) =>
        {
            var tokenError = await ResolveTokenAsync(db, http, api_token);
            if (tokenError is not null) return tokenError;
            var rows = await db.Positions.AsNoTracking().OrderByDescending(p => p.UpdatedAt)
                .Select(p => new
                {
                    p.Id, p.Title, p.Company, p.Level,
                    AttributeCount = p.Attributes.Count,
                    CvCount = p.Cvs.Count(c => c.Status == CvStatus.Published),
                    p.UpdatedAt,
                })
                .ToListAsync();
            return Results.Ok(rows);
        });

        // One position with per-attribute aggregated results (from published CVs).
        ext.MapGet("/positions/{id:int}", async (int id, ApplicationDbContext db, [FromQuery] string? api_token, HttpContext http) =>
        {
            var tokenError = await ResolveTokenAsync(db, http, api_token);
            if (tokenError is not null) return tokenError;

            var pos = await db.Positions.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new
                {
                    p.Id, p.Title, p.ShortDescription, p.Company, p.Level, p.Access, p.MaxProjects, p.UpdatedAt,
                    Attributes = p.Attributes.OrderBy(a => a.SortOrder).Select(a => new
                    {
                        a.AttributeId, a.Required, a.Section,
                        Name = a.Attribute.Name, Type = a.Attribute.Type, Category = a.Attribute.Category,
                        Options = a.Attribute.Options,
                    }).ToList(),
                })
                .FirstOrDefaultAsync();
            if (pos is null) return Results.NotFound(new { message = "Position not found." });

            var cvUserIds = await db.Cvs.AsNoTracking()
                .Where(c => c.PositionId == id && c.Status == CvStatus.Published)
                .Select(c => c.UserId)
                .Distinct()
                .ToListAsync();
            var attrIds = pos.Attributes.Select(a => a.AttributeId).ToList();
            var values = await db.AttributeValues.AsNoTracking()
                .Where(v => cvUserIds.Contains(v.UserId) && attrIds.Contains(v.AttributeId))
                .Select(v => new ValRow(v.AttributeId, v.StringValue, v.TextValue, v.ImageUrl, v.NumericValue, v.DateValue, v.PeriodStart, v.PeriodEnd, v.BoolValue, v.OptionValue))
                .ToListAsync();

            var attributes = pos.Attributes.Select(a => new
            {
                a.Name,
                Type = a.Type.ToString(),
                a.Category,
                a.Required,
                a.Section,
                a.Options,
                Aggregate = Aggregate(a.Type, values.Where(v => v.AttributeId == a.AttributeId), a.Options),
            }).ToList();

            return Results.Ok(new
            {
                pos.Id, pos.Title, pos.ShortDescription, pos.Company, pos.Level, pos.Access, pos.MaxProjects, pos.UpdatedAt,
                CvCount = cvUserIds.Count,
                Attributes = attributes,
            });
        });

        // Optional export-back: create a position from an external system (Odoo).
        ext.MapPost("/positions", async ([FromBody] ExtPositionRequest req, ApplicationDbContext db, [FromQuery] string? api_token, HttpContext http) =>
        {
            var tokenError = await ResolveTokenAsync(db, http, api_token);
            if (tokenError is not null) return tokenError;
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.Json(new { message = "Title is required." }, statusCode: 400);

            var createdBy = await db.ExternalApiTokens.AsNoTracking()
                .Where(t => t.RevokedAt == null)
                .OrderBy(t => t.Id)
                .Select(t => t.CreatedByUserId)
                .FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(createdBy)) createdBy = "";

            // Resolve attributes by name against the library, creating missing definitions
            // (Category "Imported"). OneOfMany requires its options to be usable.
            var attrReqs = new List<PositionAttributeReq>();
            var order = 0;
            foreach (var a in req.Attributes ?? [])
            {
                if (string.IsNullOrWhiteSpace(a.Name)) continue;
                var name = a.Name.Trim();
                var existing = await db.Attributes.FirstOrDefaultAsync(d => d.Name == name);
                int attrId;
                if (existing is not null)
                {
                    attrId = existing.Id;
                }
                else
                {
                    var type = Enum.TryParse<AttributeType>(a.Type, ignoreCase: true, out var t) ? t : AttributeType.String;
                    if (type == AttributeType.OneOfMany && string.IsNullOrWhiteSpace(a.Options))
                        return Results.Json(new { message = $"Attribute '{name}' is OneOfMany but has no options." }, statusCode: 400);
                    var def = new AttributeDef
                    {
                        Name = name,
                        Type = type,
                        Category = string.IsNullOrWhiteSpace(a.Category) ? "Imported" : a.Category!.Trim(),
                        Options = a.Options,
                        CreatedAt = DateTimeOffset.UtcNow,
                    };
                    db.Attributes.Add(def);
                    await db.SaveChangesAsync();
                    attrId = def.Id;
                }
                attrReqs.Add(new PositionAttributeReq(0, attrId, a.Required, a.Section));
                order++;
            }

            var upsert = new PositionUpsert(
                req.Title.Trim(), req.ShortDescription, req.Company, req.Level,
                PositionAccess.Public, req.MaxProjects <= 0 ? 5 : req.MaxProjects,
                attrReqs, [], [], null);
            var (ok, error, id) = await PositionCommands.CreateAsync(db, createdBy, upsert);
            return ok ? Results.Ok(new { id }) : Results.Json(new { message = error }, statusCode: 400);
        });
    }

    /// <summary>Creates and persists a new API token (shared by the HTTP endpoint and the position page dialog).</summary>
    public static async Task<ExternalApiToken> CreateTokenAsync(ApplicationDbContext db, string? name, string createdByUserId)
    {
        var entity = new ExternalApiToken
        {
            Token = GenerateToken(),
            Name = string.IsNullOrWhiteSpace(name) ? "external-inventory" : name.Trim(),
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ExternalApiTokens.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private static async Task<IResult?> ResolveTokenAsync(ApplicationDbContext db, HttpContext http, string? queryToken)
    {
        var provided = http.Request.Headers["X-Api-Token"].ToString();
        if (string.IsNullOrEmpty(provided)) provided = queryToken ?? "";
        if (string.IsNullOrEmpty(provided))
            return Results.Json(new { message = "API token required. Pass it as the X-Api-Token header or api_token query parameter." }, statusCode: 401);

        var token = await db.ExternalApiTokens.FirstOrDefaultAsync(t => t.Token == provided);
        if (token is null)
            return Results.Json(new { message = "Invalid API token." }, statusCode: 401);
        if (token.RevokedAt is not null)
            return Results.Json(new { message = "This API token has been revoked." }, statusCode: 403);

        token.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>Aggregates for one attribute over the published-CV values.</summary>
    private static object? Aggregate(AttributeType type, IEnumerable<ValRow> values, string? options)
    {
        var vals = values.ToList();
        switch (type)
        {
            case AttributeType.Numeric:
            {
                var nums = vals.Where(v => v.NumericValue is not null).Select(v => v.NumericValue!.Value).ToList();
                if (nums.Count == 0) return new { count = 0 };
                return new
                {
                    count = nums.Count,
                    avg = Math.Round(nums.Average(), 2),
                    min = nums.Min(),
                    max = nums.Max(),
                };
            }
            case AttributeType.OneOfMany:
            case AttributeType.String:
            {
                var texts = vals.Select(v => type == AttributeType.OneOfMany ? v.OptionValue : v.StringValue)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
                if (texts.Count == 0) return new { count = 0, top = Array.Empty<object>() };
                var top = texts.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                    .Take(5).Select(g => new { value = g.Key, count = g.Count() }).ToList();
                return new { count = texts.Count, distinct = texts.Distinct().Count(), top };
            }
            case AttributeType.Text:
            {
                var texts = vals.Where(v => !string.IsNullOrWhiteSpace(v.TextValue)).Select(v => v.TextValue!).ToList();
                if (texts.Count == 0) return new { count = 0 };
                var top = texts.GroupBy(s => s).OrderByDescending(g => g.Count())
                    .Take(3).Select(g => new { value = Truncate(g.Key, 140), count = g.Count() }).ToList();
                return new { count = texts.Count, top };
            }
            case AttributeType.Boolean:
            {
                var trues = vals.Count(v => v.BoolValue);
                return new { count = vals.Count, trues, falses = vals.Count - trues };
            }
            case AttributeType.Date:
            {
                var dates = vals.Where(v => v.DateValue is not null).Select(v => v.DateValue!.Value).ToList();
                if (dates.Count == 0) return new { count = 0 };
                return new { count = dates.Count, min = dates.Min(), max = dates.Max() };
            }
            case AttributeType.Period:
            {
                var starts = vals.Where(v => v.PeriodStart is not null).Select(v => v.PeriodStart!.Value).ToList();
                var ends = vals.Where(v => v.PeriodEnd is not null).Select(v => v.PeriodEnd!.Value).ToList();
                if (starts.Count == 0 && ends.Count == 0) return new { count = 0 };
                DateOnly? earliestStart = starts.Count > 0 ? starts.Min() : null;
                DateOnly? latestEnd = ends.Count > 0 ? ends.Max() : null;
                return new
                {
                    count = Math.Max(starts.Count, ends.Count),
                    earliestStart,
                    latestEnd,
                };
            }
            case AttributeType.Image:
                return new { count = vals.Count(v => !string.IsNullOrWhiteSpace(v.ImageUrl)) };
            default:
                return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public sealed record CreateTokenRequest(string? Name);
    public sealed record ValRow(int AttributeId, string? StringValue, string? TextValue, string? ImageUrl, double? NumericValue,
        DateOnly? DateValue, DateOnly? PeriodStart, DateOnly? PeriodEnd, bool BoolValue, string? OptionValue);
}

public sealed record ExtPositionRequest(
    string Title, string? ShortDescription, string? Company, string? Level,
    int MaxProjects, List<ExtAttributeReq>? Attributes);

public sealed record ExtAttributeReq(string Name, string? Type, string? Category, bool Required, string? Section, string? Options);
