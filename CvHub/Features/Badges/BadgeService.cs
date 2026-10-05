using System.Text;
using System.Text.Encodings.Web;
using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Badges;

/// <summary>
/// Achievement badges derived from a user's activity (optional requirement #3).
/// Rules use the spec's examples: 10 projects, 5 CVs, 25 likes — plus a couple of
/// progression badges. Counts are read live from the database (no extra schema).
/// </summary>
public sealed record Badge(string Id, string Name, string Description, string Color, string SvgPath);

public static class BadgeService
{
    public static readonly Badge[] Catalog =
    [
        new("projects_10", "Project Master", "Created 10 projects", "#f59e0b",
            "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25 1.18-6.88-5-4.87L12 8.26z"),
        new("cvs_5", "CV Builder", "Created 5 CVs", "#10b981", "M6 4h12v16H6z"),
        new("likes_25", "Popular", "Received 25 likes on your CVs", "#ef4444",
            "M12 21.35l-1.45-1.32C5.4 15.38 2 11.9 2 7.2 2 4.8 3.8 3 6 3c1.34 0 2.61.5 3.57 1.34L12 6.5l2.43-2.16C15.39 3.5 16.66 3 18 3c2.2 0 4 1.8 4 4 0 4.7-3.4 8.18-6.55 10.29l-1.45 1.32z"),
        new("first_cv", "First Step", "Published your first CV", "#3b82f6",
            "M12 2a10 10 0 100 20 10 10 0 000-20z"),
        new("likes_5_given", "Appreciative", "Liked 5 CVs", "#8b5cf6",
            "M6.01 12.01L6 14l2 2 4-4"),
    ];

    public static async Task<Badge[]> EarnedAsync(ApplicationDbContext db, string userId)
    {
        // Single aggregate round-trip instead of five sequential queries.
        var m = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                Projects = db.Projects.Count(p => p.UserId == userId),
                Cvs = db.Cvs.Count(c => c.UserId == userId),
                LikesReceived = db.Likes.Count(l => db.Cvs.Any(c => c.Id == l.CvId && c.UserId == userId)),
                LikesGiven = db.Likes.Count(l => l.UserId == userId),
                HasPublishedCv = db.Cvs.Any(c => c.UserId == userId && c.Status == CvStatus.Published),
            })
            .FirstOrDefaultAsync();
        if (m is null) return [];

        var earned = new List<Badge>();
        if (m.Projects >= 10) earned.Add(Catalog[0]);
        if (m.Cvs >= 5) earned.Add(Catalog[1]);
        if (m.LikesReceived >= 25) earned.Add(Catalog[2]);
        if (m.HasPublishedCv) earned.Add(Catalog[3]);
        if (m.LikesGiven >= 5) earned.Add(Catalog[4]);
        return earned.ToArray();
    }

    /// <summary>Renders earned badges as a standalone, downloadable SVG panel.</summary>
    public static string RenderPanel(Badge[] badges)
    {
        if (badges.Length == 0)
            return "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'></svg>";

        var enc = HtmlEncoder.Default;
        var sb = new StringBuilder();
        const int step = 136, w = 640;
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='120' viewBox='0 0 {w} 120' font-family='ui-serif,Georgia,serif'>");
        sb.Append("<rect width='100%' height='120' rx='16' fill='#0f172a'/>");
        sb.Append("<text x='20' y='26' font-size='16' font-weight='600' fill='#f8fafc'>Badges</text>");
        for (int i = 0; i < badges.Length; i++)
        {
            var b = badges[i];
            var x = 20 + i * step;
            sb.Append($"<g transform='translate({x},48)'>");
            sb.Append($"<path d='{b.SvgPath}' fill='{b.Color}' transform='scale(1.5)'/>");
            sb.Append($"<text x='28' y='3' font-size='13' font-weight='600' fill='#f8fafc'>{enc.Encode(b.Name)}</text>");
            sb.Append($"<text x='28' y='18' font-size='10' fill='#cbd5e1'>{enc.Encode(b.Description)}</text>");
            sb.Append("</g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
