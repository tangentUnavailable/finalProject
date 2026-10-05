using System.Security.Claims;

namespace CvHub.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("Admin");
    public static bool IsRecruiter(this ClaimsPrincipal user) => user.IsInRole("Recruiter");
    public static bool IsRecruiterOrAdmin(this ClaimsPrincipal user) => user.IsInRole("Recruiter") || user.IsInRole("Admin");
}
