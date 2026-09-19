using CvHub.Data;
using CvHub.Domain;
using CvHub.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Discussions;

public static class CommentsApi
{
    public static void MapCommentsApi(this IEndpointRouteBuilder app)
    {
        var commentGroup = app.MapGroup("").DisableAntiforgery();

        commentGroup.MapGet("/api/positions/{id:int}/comments", async (int id, ApplicationDbContext db,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var isRecruiter = http.User.IsInRole("Recruiter") || http.User.IsInRole("Admin");
            var posts = await db.Comments.Where(c => c.PositionId == id)
                .OrderBy(c => c.CreatedAt)
                .Take(500)
                .Select(c => new
                {
                    c.Id,
                    c.Body,
                    c.CreatedAt,
                    c.UserId,
                    AuthorName = db.Users.Where(u => u.Id == c.UserId).Select(u => u.DisplayName ?? u.UserName).FirstOrDefault(),
                })
                .ToListAsync();

            return Results.Ok(posts.Select(p => new
            {
                p.Id,
                p.Body,
                Html = MarkdownRenderer.Render(p.Body),
                p.CreatedAt,
                AuthorId = isRecruiter ? p.UserId : null,
                AuthorName = p.AuthorName ?? "user",
                IsRecruiterView = isRecruiter,
            }));
        });

        commentGroup.MapPost("/api/positions/{id:int}/comments", async (int id, [FromBody] PostRequest req,
            ApplicationDbContext db, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            if (string.IsNullOrWhiteSpace(req.Body)) return Results.Json(new { message = "Post body is required." }, statusCode: 400);

            var exists = await db.Positions.AnyAsync(p => p.Id == id);
            if (!exists) return Results.Json(new { message = "Position not found." }, statusCode: 404);

            db.Comments.Add(new Comment { PositionId = id, UserId = uid, Body = req.Body, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}

public record PostRequest(string Body);
