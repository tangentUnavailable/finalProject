using CvHub.Data;
using CvHub.Domain;
using CvHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Infrastructure;

/// <summary>Shared core logic for creating a CV (used by the API endpoint and the position page).</summary>
public static class CvFactory
{
    /// <summary>
    /// Creates a CV for the given user and position, enforcing access rules and
    /// the one-CV-per-position constraint, then pre-selects matching projects.
    /// </summary>
    public static async Task<(int? CvId, string? Error)> CreateAsync(
        ApplicationDbContext db, string userId, int positionId, bool isRecruiterOrAdmin)
    {
        var pos = await db.Positions.Include(p => p.Filters).ThenInclude(f => f.Attribute)
            .FirstOrDefaultAsync(p => p.Id == positionId);
        if (pos is null) return (null, "Position not found.");

        // Recruiters (and admins acting as recruiters) can only view published CVs, not create them.
        // Only candidates (non-recruiter, non-admin) can create CVs for positions they have access to.
        var isCandidate = !isRecruiterOrAdmin;
        if (isCandidate)
        {
            var values = await db.AttributeValues.Where(v => v.UserId == userId)
                .ToDictionaryAsync(v => v.AttributeId);
            if (!AccessRules.HasAccess(pos, values, false))
                return (null, "You do not have access to this position.");
        }
        else
        {
            return (null, "Recruiters and admins cannot create CVs. CVs are created by candidates.");
        }

        if (await db.Cvs.AnyAsync(c => c.PositionId == positionId && c.UserId == userId))
            return (null, "You already have a CV for this position.");

        var cv = new Cv { PositionId = positionId, UserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        db.Cvs.Add(cv);
        await db.SaveChangesAsync();

        // Pre-select the candidate's projects matching the position tags, up to the cap.
        var tagIds = await db.PositionTags.Where(pt => pt.PositionId == pos.Id).Select(pt => pt.TagId).ToListAsync();
        var max = pos.MaxProjects > 0 ? pos.MaxProjects : 5;
        var projects = await db.Projects
            .Where(p => p.UserId == userId && p.Tags.Any(t => tagIds.Contains(t.TagId)))
            .OrderBy(p => p.SortOrder).Take(max).Select(p => p.Id).ToListAsync();
        db.CvProjects.AddRange(projects.Select(pId => new CvProject { CvId = cv.Id, ProjectId = pId }));
        await db.SaveChangesAsync();

        return (cv.Id, null);
    }
}
