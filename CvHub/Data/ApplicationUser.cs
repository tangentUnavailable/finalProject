using Microsoft.AspNetCore.Identity;

namespace CvHub.Data;

/// <summary>Application user with role support and block flag.</summary>
public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool IsBlocked { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
