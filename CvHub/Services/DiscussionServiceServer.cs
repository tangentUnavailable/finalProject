using CvHub.Data;
using CvHub.Features.Discussions;
using CvHub.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Services;

/// <summary>Database-backed discussion access for server-side interactive rendering.</summary>
public sealed class DiscussionServiceServer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuthenticationStateProvider authState) : IDiscussionService
{
    public async Task<List<IDiscussionService.PostDto>> ListAsync(int positionId, bool isRecruiterView, CancellationToken ct = default)
    {
        var (_, _, posts) = await CommentCommands.ListAsync(dbFactory, positionId, isRecruiterView, ct);
        return posts.Select(p => new IDiscussionService.PostDto(p.Id, p.Body, p.Html, p.CreatedAt, p.AuthorId, p.AuthorName, p.IsRecruiterView)).ToList();
    }

    public async Task<(bool Ok, string? Error)> PostAsync(int positionId, string body, CancellationToken ct = default)
    {
        var uid = await CurrentUserIdAsync();
        if (uid is null) return (false, "Sign in required.");
        return await CommentCommands.AddAsync(dbFactory, positionId, uid, body, ct);
    }

    private async Task<string?> CurrentUserIdAsync()
    {
        var auth = await authState.GetAuthenticationStateAsync();
        return auth.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}
