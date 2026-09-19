using CvHub.Data;
using CvHub.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Services;

/// <summary>Database-backed attribute lookups for server-side interactive rendering.</summary>
public sealed class AttributeLibraryServer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuthenticationStateProvider authState) : IAttributeLibrary
{
    public async Task<List<IAttributeLibrary.AttributeRow>> SearchAsync(string? prefix, string? category, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Attributes.AsQueryable();
        if (!string.IsNullOrEmpty(category))
            q = q.Where(a => a.Category == category);
        if (!string.IsNullOrEmpty(prefix))
            q = q.Where(a => EF.Functions.ILike(a.Name, prefix + "%"));
        return await q.OrderBy(a => a.Name).Take(30)
            .Select(a => new IAttributeLibrary.AttributeRow(a.Id, a.Name, a.Category, a.Type))
            .ToListAsync(ct);
    }

    public async Task<List<IAttributeLibrary.AttributeRow>> RecentAsync(string? prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(prefix))
            return [];
        var uid = await CurrentUserIdAsync();
        if (uid is null)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RecentAttributes.Where(r => r.UserId == uid)
            .OrderByDescending(r => r.UsedAt).Take(5)
            .Join(db.Attributes, r => r.AttributeId, a => a.Id,
                (r, a) => new IAttributeLibrary.AttributeRow(a.Id, a.Name, a.Category, a.Type))
            .ToListAsync(ct);
    }

    public async Task<HashSet<int>> PinnedAsync(CancellationToken ct = default)
    {
        var uid = await CurrentUserIdAsync();
        if (uid is null)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.ProfileAttributes.AsNoTracking()
            .Where(pa => pa.UserId == uid).Select(pa => pa.AttributeId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    private async Task<string?> CurrentUserIdAsync()
    {
        var auth = await authState.GetAuthenticationStateAsync();
        return auth.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}
