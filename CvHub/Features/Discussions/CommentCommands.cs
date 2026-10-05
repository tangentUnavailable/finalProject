using CvHub.Data;
using CvHub.Domain;
using CvHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Discussions;

/// <summary>Shared comment logic for the JSON API and server-rendered components.</summary>
public static class CommentCommands
{
    public static async Task<(bool Ok, string? Error, List<PostDto> Posts)> ListAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int positionId, bool isRecruiter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var posts = await db.Comments.Where(c => c.PositionId == positionId)
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
            .ToListAsync(ct);

        return (true, null, posts.Select(p => new PostDto(
            p.Id,
            p.Body,
            MarkdownRenderer.Render(p.Body),
            p.CreatedAt,
            isRecruiter ? p.UserId : null,
            p.AuthorName ?? "user",
            isRecruiter)).ToList());
    }

    public static async Task<(bool Ok, string? Error)> AddAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int positionId, string userId, string body, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return (false, "Post body is required.");
        var exists = await db.Positions.AnyAsync(p => p.Id == positionId, ct);
        if (!exists)
            return (false, "Position not found.");

        db.Comments.Add(new Comment { PositionId = positionId, UserId = userId, Body = body, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public sealed record PostDto(
        int Id, string Body, string Html, DateTimeOffset CreatedAt,
        string? AuthorId, string AuthorName, bool IsRecruiterView);
}
