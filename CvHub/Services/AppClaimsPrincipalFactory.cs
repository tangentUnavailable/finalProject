using System.Security.Claims;
using CvHub.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CvHub.Services;

/// <summary>Adds the friendly display name as a claim and keeps role claims working.</summary>
public class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim("display_name", user.DisplayName ?? user.Email ?? "user"));
        // Blocked users carry a claim so stale sessions fail closed at the security
        // stamp check, and so future middleware can reject without a DB hit.
        if (user.IsBlocked)
            identity.AddClaim(new Claim("is_blocked", "true"));
        return identity;
    }
}
