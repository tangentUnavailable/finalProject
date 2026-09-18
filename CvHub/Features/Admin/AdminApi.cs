using CvHub.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Admin;

public static class AdminApi
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization(p => p.RequireRole("Admin")).DisableAntiforgery();

        group.MapGet("/", async (ApplicationDbContext db) =>
            await db.Users.OrderBy(u => u.Email).Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.IsBlocked, u.CreatedAt,
                Roles = db.UserRoles.Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!).ToList(),
            }).ToListAsync());

        group.MapPost("/{id}/block/{blocked:bool}", async (string id, bool blocked, ApplicationDbContext db) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();
            user.IsBlocked = blocked;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPost("/{id}/roles/{role}/{on:bool}", async (string id, string role, bool on,
            UserManager<ApplicationUser> users, ApplicationDbContext db) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            var result = on ? await users.AddToRoleAsync(user, role) : await users.RemoveFromRoleAsync(user, role);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Errors);
        });

        group.MapDelete("/{id}", async (string id, UserManager<ApplicationUser> users, HttpContext http) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            await users.DeleteAsync(user);
            return Results.Ok();
        });
    }
}
