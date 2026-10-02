using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Services;

public class IdentitySeeder(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ApplicationDbContext db,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger)
{
    public static class Roles
    {
        public const string Admin = "Admin";
        public const string Recruiter = "Recruiter";
        public const string Candidate = "Candidate";
    }

    public async Task SeedAsync()
    {
        foreach (var role in new[] { Roles.Admin, Roles.Recruiter, Roles.Candidate })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // The real admin account receives the Power Automate support-ticket e-mails
        // (the ticket JSON's admin_emails lists every user in the Admin role).
        // Address, display name and password all come from configuration (Seed:Admin*)
        // instead of being hardcoded, so no personal address or known password is baked
        // into the repository. When they are not configured we skip creation entirely
        // rather than fall back to a guessable default.
        var adminEmail = configuration["Seed:AdminEmail"]?.Trim();
        var adminPassword = configuration["Seed:AdminPassword"];
        var adminName = configuration["Seed:AdminDisplayName"]?.Trim();
        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogWarning(
                "Seed:AdminEmail and Seed:AdminPassword are not configured — skipping admin account creation. " +
                "Set them in appsettings.json or via Seed__AdminEmail / Seed__AdminPassword environment variables.");
        }
        else if (await userManager.FindByEmailAsync(adminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                DisplayName = string.IsNullOrWhiteSpace(adminName) ? adminEmail : adminName,
            };
            var created = await userManager.CreateAsync(admin, adminPassword);
            if (created.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, Roles.Admin);
                logger.LogInformation("Seeded admin account {AdminEmail}.", adminEmail);
            }
            else
            {
                logger.LogError("Could not seed admin account: {Errors}",
                    string.Join("; ", created.Errors.Select(e => e.Description)));
            }
        }

        // Demo accounts for evaluation.
        await EnsureDemoUserAsync("recruiter@cvhub.local", "Recruiter123!", "Rachel Recruiter", Roles.Recruiter);
        await EnsureDemoUserAsync("candidate@cvhub.local", "Candidate123!", "Carl Candidate", Roles.Candidate);
        await EnsureDemoUserAsync("candidate2@cvhub.local", "Candidate123!", "Carla Candidate", Roles.Candidate);

        // Built-in "Me" attributes: built on the same engine, but undeletable by recruiters.
        if (!await db.Attributes.AnyAsync(a => a.IsBuiltIn))
        {
            db.Attributes.AddRange(
                new AttributeDef { Name = BuiltInAttributeNames.FirstName, Category = "Personal Information", Type = AttributeType.String, IsBuiltIn = true, Description = "Given name", CreatedAt = DateTimeOffset.UtcNow },
                new AttributeDef { Name = BuiltInAttributeNames.LastName, Category = "Personal Information", Type = AttributeType.String, IsBuiltIn = true, Description = "Family name", CreatedAt = DateTimeOffset.UtcNow },
                new AttributeDef { Name = BuiltInAttributeNames.Location, Category = "Personal Information", Type = AttributeType.String, IsBuiltIn = true, Description = "City, country", CreatedAt = DateTimeOffset.UtcNow },
                new AttributeDef { Name = BuiltInAttributeNames.Photo, Category = "Personal Information", Type = AttributeType.Image, IsBuiltIn = true, Description = "Profile photo", CreatedAt = DateTimeOffset.UtcNow },
                new AttributeDef { Name = BuiltInAttributeNames.Headline, Category = "Personal Information", Type = AttributeType.String, IsBuiltIn = true, Description = "Professional headline", CreatedAt = DateTimeOffset.UtcNow },
                new AttributeDef { Name = BuiltInAttributeNames.About, Category = "Personal Information", Type = AttributeType.Text, IsBuiltIn = true, Description = "Short bio (Markdown)", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    private async Task EnsureDemoUserAsync(string email, string password, string displayName, string role)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName };
            await userManager.CreateAsync(user, password);
        }
        if (!await userManager.IsInRoleAsync(user, role))
            await userManager.AddToRoleAsync(user, role);
    }
}
