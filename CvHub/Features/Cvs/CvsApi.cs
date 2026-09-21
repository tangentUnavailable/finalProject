using CvHub.Data;
using CvHub.Domain;
using CvHub.Infrastructure;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Cvs;

public static class CvsApi
{
    public static void MapCvsApi(this IEndpointRouteBuilder app)
    {
        var cvGroup = app.MapGroup("").DisableAntiforgery();

        // Candidate creates a CV for an accessible position (at most one per position).
        cvGroup.MapPost("/api/cvs", async ([FromBody] CreateCvRequest req, ApplicationDbContext db,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);

            var isAdmin = http.User.IsAdmin();
            var (cvId, error) = await CvFactory.CreateAsync(db, uid, req.PositionId, isAdmin || http.User.IsRecruiter());
            return error is null
                ? Results.Ok(new { id = cvId })
                : Results.Json(new { message = error }, statusCode: error.Contains("access") ? 403 : 409);
        });

        // One composed CV (used for in-place refresh after project selection).
        cvGroup.MapGet("/api/cvs/{id:int}", async (int id, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            var cv = await db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cv is null) return Results.NotFound();
            var isAdmin = http.User.IsAdmin();
            var isRecruiter = http.User.IsRecruiter();
            if (cv.UserId != uid && !isAdmin && !(isRecruiter && cv.Status == CvStatus.Published))
                return Results.Json(new { message = "Not allowed." }, statusCode: 403);
            return Results.Ok(await CvComposer.ComposeAsync(db, cv));
        });

        // ---------- Project selection for one CV (owner or admin) ----------
        cvGroup.MapGet("/api/cvs/{id:int}/projects", async (int id, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            var cv = await db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cv is null) return Results.NotFound();
            if (cv.UserId != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Not your CV." }, statusCode: 403);

            var selected = await CvService.GetSelectedProjectsAsync(db, id);
            return Results.Ok(new { selected });
        });

        cvGroup.MapPut("/api/cvs/{id:int}/projects", async (int id, [FromBody] UpdateCvProjectsRequest req,
            ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            var cv = await db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cv is null) return Results.NotFound();
            if (cv.UserId != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Not your CV." }, statusCode: 403);

            await CvService.UpdateProjectsAsync(db, id, req.ProjectIds);
            return Results.Ok(new { selected = req.ProjectIds });
        });

        // CVs for a position (recruiter/admin only), table with aggregates.
        cvGroup.MapGet("/api/positions/{id:int}/cvs", async (int id, ApplicationDbContext db,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null || !http.User.IsRecruiterOrAdmin())
                return Results.Json(new { message = "Recruiters only." }, statusCode: 403);
            return Results.Ok(
                await db.Cvs.Where(c => c.PositionId == id && c.Status == CvStatus.Published)
                .Select(c => new
                {
                    c.Id,
                    c.CreatedAt,
                    c.PublishedAt,
                    Likes = db.Likes.Count(l => l.CvId == c.Id),
                    ProjectCount = c.Projects.Count,
                })
                .ToListAsync());
        });

        // Export published CVs for a position to CSV (recruiters/admin) — optional req #5.
        cvGroup.MapGet("/api/positions/{id:int}/cvs/export", async (int id, ApplicationDbContext db,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null || !http.User.IsRecruiterOrAdmin())
                return Results.Json(new { message = "Recruiters only." }, statusCode: 403);

            var cvs = await db.Cvs.Where(c => c.PositionId == id && c.Status == CvStatus.Published)
                .OrderBy(c => c.CreatedAt).ToListAsync();
            var ownerIds = cvs.Select(c => c.UserId).Distinct().ToList();
            var ownerMap = await db.Users.Where(u => ownerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id);
            var posAttrs = await db.PositionAttributes.Where(pa => pa.PositionId == id)
                .OrderBy(pa => pa.SortOrder).Select(pa => pa.Attribute.Name).ToListAsync();

            var sb = new System.Text.StringBuilder();
            var header = new List<string> { "Candidate", "Email", "Status" };
            header.AddRange(posAttrs.Select(name => CvDisplay.CsvCell(name)));
            sb.AppendLine(string.Join(",", header));
            foreach (var cv in cvs)
            {
                var view = await CvComposer.ComposeAsync(db, cv);
                if (view is null) continue;
                var owner = ownerMap.TryGetValue(cv.UserId, out var u) ? (u.DisplayName ?? u.Email ?? "") : "";
                var email = ownerMap.TryGetValue(cv.UserId, out var u2) ? (u2.Email ?? "") : "";
                var cells = new List<string>
                {
                    CvDisplay.CsvCell(owner),
                    CvDisplay.CsvCell(email),
                    CvDisplay.CsvCell(cv.Status.ToString())
                };
                cells.AddRange(view.Fields.Select(f => CvDisplay.CsvCell(CvDisplay.FieldValue(f))));
                sb.AppendLine(string.Join(",", cells));
            }
            return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
                "text/csv; charset=utf-8", $"cvs-export-position-{id}.csv");
        });

        // PDF export of a CV (owner/admin) with a QR code linking back to the app — optional req #1.
        cvGroup.MapGet("/api/cvs/{id:int}/pdf", async (int id, ApplicationDbContext db,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            var cv = await db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cv is null) return Results.NotFound();
            if (cv.UserId != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Not allowed." }, statusCode: 403);

            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            var pdf = await CvPdfGenerator.GenerateAsync(db, cv, baseUrl);
            return Results.File(pdf, "application/pdf", $"cv-{id}.pdf");
        });

        // Publish: allowed only when all required fields are filled.
        cvGroup.MapPost("/api/cvs/{id:int}/publish", async (int id, ApplicationDbContext db,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var cv = await db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cv is null) return Results.NotFound();
            if (cv.UserId != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Not your CV." }, statusCode: 403);

            var (ok, error) = await CvService.PublishAsync(db, id);
            return ok ? Results.Ok() : Results.Json(new { message = error }, statusCode: 409);
        });

        // Like / unlike (recruiters only, one per recruiter per CV).
        cvGroup.MapPost("/api/cvs/{id:int}/like", async (int id, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            if (!http.User.IsRecruiterOrAdmin())
                return Results.Json(new { message = "Only recruiters may like CVs." }, statusCode: 403);

            var (liked, count) = await CvService.ToggleLikeAsync(db, uid, id);
            return Results.Ok(new { liked, count });
        });

        // Delete own CV (candidate) or any CV (admin).
        cvGroup.MapDelete("/api/cvs/{id:int}", async (int id, ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var cv = await db.Cvs.FirstOrDefaultAsync(c => c.Id == id);
            if (cv is null) return Results.NotFound();
            if (cv.UserId != uid && !http.User.IsAdmin())
                return Results.Json(new { message = "Not your CV." }, statusCode: 403);
            db.Cvs.Remove(cv);
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}

public record CreateCvRequest(int PositionId, int? MaxProjects);
public record UpdateCvProjectsRequest(List<int> ProjectIds);
