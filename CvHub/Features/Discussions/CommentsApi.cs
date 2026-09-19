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

        commentGroup.MapGet("/api/positions/{id:int}/comments", async (int id,
            IDbContextFactory<ApplicationDbContext> dbFactory,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var isRecruiter = http.User.IsInRole("Recruiter") || http.User.IsInRole("Admin");
            var (_, _, posts) = await CommentCommands.ListAsync(dbFactory, id, isRecruiter);
            return Results.Ok(posts);
        });

        commentGroup.MapPost("/api/positions/{id:int}/comments", async (int id, [FromBody] PostRequest req,
            IDbContextFactory<ApplicationDbContext> dbFactory,
            UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var uid = users.GetUserId(http.User);
            if (uid is null) return Results.Json(new { message = "Sign in required." }, statusCode: 401);
            var (ok, error) = await CommentCommands.AddAsync(dbFactory, id, uid, req.Body);
            if (!ok) return Results.Json(new { message = error }, statusCode: error == "Position not found." ? 404 : 400);
            return Results.Ok();
        });
    }
}

public record PostRequest(string Body);
