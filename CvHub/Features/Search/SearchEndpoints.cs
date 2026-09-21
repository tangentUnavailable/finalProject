using CvHub.Data;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Search;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (string q, string? scope, ApplicationDbContext db) =>
            Results.Ok(await QueryAsync(db, q, scope ?? "all")));
    }

    /// <summary>Shared search implementation (used by the API endpoint and the results page).</summary>
    public static async Task<SearchResults> QueryAsync(ApplicationDbContext db, string q, string scope)
    {
        var results = new SearchResults();
        if (string.IsNullOrWhiteSpace(q)) return results;

        // Hits the store-generated search_vector column (GIN-indexed) instead of recomputing tsvector per row.
        results.Positions = await db.Positions.AsNoTracking()
            .Where(p => p.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", q)))
            .OrderByDescending(p => p.UpdatedAt)
            .Take(20)
            .Select(p => new Hit(p.Id.ToString(), p.Title, p.Company, "positions/" + p.Id, "position"))
            .ToListAsync();

        if (scope is "all" or "attributes")
        {
            results.Attributes = await db.Attributes.AsNoTracking()
                .Where(a => EF.Functions.ToTsVector("simple", a.Name + " " + (a.Description ?? "") + " " + a.Category)
                            .Matches(EF.Functions.WebSearchToTsQuery("simple", q)))
                .OrderBy(a => a.Name).Take(20)
                .Select(a => new Hit(a.Id.ToString(), a.Name, a.Category, "attributes", "attribute"))
                .ToListAsync();
        }

        results.Tags = await db.Tags.AsNoTracking()
            .Where(t => t.Name.ToLower().StartsWith(q.ToLower()))
            .Take(10)
            .Select(t => new Hit(t.Id.ToString(), t.Name, null, "search?q=" + Uri.EscapeDataString(t.Name), "tag"))
            .ToListAsync();

        return results;
    }

    /// <summary>
    /// CV full-text search for Recruiters/Admins: matches published CV content —
    /// candidate name (profile values) and project names/descriptions.
    /// One aggregate SQL query (no per-row scans, no queries in loops).
    /// </summary>
    public static Task<List<Hit>> SearchCvsAsync(ApplicationDbContext db, string q) =>
        db.Cvs.AsNoTracking()
            .Where(c => c.Status == CvStatus.Published && !c.IsDeleted)
            .Where(c =>
                // Any profile attribute value of the CV owner matches (incl. name attributes).
                db.AttributeValues.Any(v => v.UserId == c.UserId &&
                    ((v.StringValue != null && EF.Functions.ToTsVector("simple", v.StringValue).Matches(EF.Functions.WebSearchToTsQuery("simple", q))) ||
                     (v.TextValue != null && EF.Functions.ToTsVector("simple", v.TextValue).Matches(EF.Functions.WebSearchToTsQuery("simple", q))) ||
                     (v.OptionValue != null && EF.Functions.ToTsVector("simple", v.OptionValue).Matches(EF.Functions.WebSearchToTsQuery("simple", q)))))
                // Any project name/description of the CV owner matches.
                || db.Projects.Any(p => p.UserId == c.UserId &&
                    EF.Functions.ToTsVector("simple", p.Name + " " + p.Description).Matches(EF.Functions.WebSearchToTsQuery("simple", q))))
            .OrderByDescending(c => c.Id)
            .Take(20)
            .Select(c => new Hit(
                c.Id.ToString(),
                db.AttributeValues.Where(v => v.UserId == c.UserId && v.Attribute.Name == "First Name")
                    .Select(v => v.StringValue).FirstOrDefault() ?? "",
                (db.AttributeValues.Where(v => v.UserId == c.UserId && v.Attribute.Name == "Last Name")
                    .Select(v => v.StringValue).FirstOrDefault() ?? "")
                    + " — " + c.Position.Title,
                "cv/" + c.Id,
                "cv",
                db.Likes.Count(l => l.CvId == c.Id)))
            .ToListAsync();

    public sealed class SearchResults
    {
        public List<Hit> Positions { get; set; } = [];
        public List<Hit> Attributes { get; set; } = [];
        public List<Hit> Tags { get; set; } = [];
        public List<Hit> Cvs { get; set; } = [];
    }

    public sealed record Hit(string Id, string Title, string? Subtitle, string Href, string Kind, int Likes = 0);
}
