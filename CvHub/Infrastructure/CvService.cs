using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Infrastructure;

/// <summary>Shared core logic for CV project selection, publishing, and liking.</summary>
public static class CvService
{
    /// <summary>Replaces the CV's selected projects, enforcing the position cap. Returns false if the CV is missing.</summary>
    public static async Task<bool> UpdateProjectsAsync(ApplicationDbContext db, int cvId, List<int> projectIds)
    {
        var cv = await db.Cvs.Include(c => c.Position).FirstOrDefaultAsync(c => c.Id == cvId);
        if (cv is null) return false;

        var existing = await db.CvProjects.Where(cp => cp.CvId == cvId).ToListAsync();
        var cap = cv.Position.MaxProjects > 0 ? cv.Position.MaxProjects : 5;
        var ids = projectIds.Distinct().Take(cap).ToList();

        db.CvProjects.RemoveRange(existing.Where(cp => !ids.Contains(cp.ProjectId)));
        foreach (var pid in ids.Where(pid => existing.All(cp => cp.ProjectId != pid)))
            db.CvProjects.Add(new CvProject { CvId = cvId, ProjectId = pid });

        await db.SaveChangesAsync();
        return true;
    }

    public static async Task<HashSet<int>> GetSelectedProjectsAsync(ApplicationDbContext db, int cvId) =>
        (await db.CvProjects.Where(cp => cp.CvId == cvId).Select(cp => cp.ProjectId).ToListAsync()).ToHashSet();

    /// <summary>Publishes a CV if all required fields are filled. Returns (ok, error).</summary>
    public static async Task<(bool Ok, string? Error)> PublishAsync(ApplicationDbContext db, int cvId)
    {
        var cv = await db.Cvs.FirstOrDefaultAsync(c => c.Id == cvId);
        if (cv is null) return (false, "CV not found.");
        var view = await CvComposer.ComposeAsync(db, cv);
        if (view is null) return (false, "CV not found.");
        if (view.MissingRequired > 0)
            return (false, $"Fill all required fields first ({view.MissingRequired} empty).");
        cv.Status = CvStatus.Published;
        cv.PublishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Toggles the current user's like. Returns the new state. Recruiters/Admins only.</summary>
    public static async Task<(bool Liked, int Count)> ToggleLikeAsync(ApplicationDbContext db, string userId, int cvId)
    {
        var like = await db.Likes.FirstOrDefaultAsync(l => l.CvId == cvId && l.UserId == userId);
        if (like is not null) db.Likes.Remove(like);
        else db.Likes.Add(new CvLike { CvId = cvId, UserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var count = await db.Likes.CountAsync(l => l.CvId == cvId);
        return (like is null, count);
    }
}
