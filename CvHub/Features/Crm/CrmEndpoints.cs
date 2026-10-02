using CvHub.Data;
using CvHub.Infrastructure;
using CvHub.Services;
using CvHub.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Features.Crm;

public static class CrmEndpoints
{
    private static (string? Uid, bool IsAdmin) Actor(HttpContext http)
    {
        var uid = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return (uid, http.User.IsAdmin());
    }

    /// <summary>Resolve whose CRM link to operate on: self, or (admins) the given ownerUserId.</span></summary>
    private static string? ResolveOwner(HttpContext http, string? ownerUserId)
    {
        var (uid, isAdmin) = Actor(http);
        if (uid is null) return null;
        if (string.IsNullOrEmpty(ownerUserId) || ownerUserId == uid) return uid;
        return isAdmin ? ownerUserId : null; // non-admins may only act on themselves
    }

    public static void MapCrmEndpoints(this IEndpointRouteBuilder app)
    {
        // Step 1 of the web-server flow: redirect the user to Salesforce consent.
        // Admins may pass ?userId= to connect on behalf of another user.
        app.MapGet("/api/crm/salesforce/connect", (HttpContext http, SalesforceService sf) =>
        {
            var owner = ResolveOwner(http, http.Request.Query["userId"].ToString());
            if (owner is null) return Results.Json(new { message = "Not allowed." }, statusCode: 403);

            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Redirect(sf.BuildAuthorizeUrl(baseUrl, owner));
        }).RequireRateLimiting("crm");

        // Step 2: Salesforce redirects back with ?code=...&state=state.userId
        app.MapGet("/signin-salesforce", async (HttpContext http, SalesforceService sf,
            IDbContextFactory<ApplicationDbContext> dbFactory) =>
        {
            var code = http.Request.Query["code"].ToString();
            var state = http.Request.Query["state"].ToString();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Redirect("/profile?crm=error");

            var parts = state.Split('.', 2);
            var stateKey = parts[0];
            var uid = parts.Length > 1 ? parts[1] : "";

            // The owner id comes from the signed state; only accept it when the callback
            // user is the owner themselves or an admin (defense in depth vs. forged states).
            var owner = ResolveOwner(http, uid);
            if (owner is null)
                return Results.Json(new { message = "Not allowed." }, statusCode: 403);

            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            try
            {
                var verifier = sf.TakeCodeVerifier(stateKey);
                if (verifier is null) return Results.Redirect("/profile?crm=error");
                var token = await sf.ExchangeCodeAsync(code, baseUrl, verifier);
                await using var db = await dbFactory.CreateDbContextAsync();
                var link = await db.CrmLinks.FirstOrDefaultAsync(l => l.UserId == owner);
                if (link is null)
                {
                    link = new Domain.CrmLink { UserId = owner };
                    db.CrmLinks.Add(link);
                }
                link.RefreshToken = token.refresh_token;
                link.InstanceUrl = token.instance_url ?? "";
                await db.SaveChangesAsync();
                return Results.Redirect("/profile?crm=connected");
            }
            catch (Exception)
            {
                return Results.Redirect("/profile?crm=error");
            }
        }).RequireRateLimiting("crm");

        // Step 3: the profile form posts here to create/update the Account + Contact.
        app.MapPost("/api/crm/salesforce/sync", async ([FromBody] CrmSyncRequest req, HttpContext http,
            SalesforceService sf, IDbContextFactory<ApplicationDbContext> dbFactory) =>
        {
            var owner = ResolveOwner(http, req.OwnerUserId);
            if (owner is null) return Results.Json(new { message = "Not allowed." }, statusCode: 403);

            await using var db = await dbFactory.CreateDbContextAsync();
            if (!sf.CentralReady)
            {
                var link = await db.CrmLinks.FirstOrDefaultAsync(l => l.UserId == owner);
                if (link is null || string.IsNullOrEmpty(link.RefreshToken))
                    return Results.Json(new { message = "Connect Salesforce first.", connectUrl = "/api/crm/salesforce/connect" }, statusCode: 409);
            }

            var result = await sf.SyncAsync(db, owner, new CrmSyncInput(req.NewsletterOptIn, req.Interests, req.Notes));
            return result.Success
                ? Results.Ok(new { result.AccountId, result.ContactId, result.InstanceUrl, syncedAt = DateTimeOffset.UtcNow })
                : Results.Json(new { message = result.Error }, statusCode: 502);
        }).RequireRateLimiting("crm");

        // Link status for the profile page badge + button state (?userId= for admins).
        app.MapGet("/api/crm/status", async (HttpContext http, SalesforceService sf, IDbContextFactory<ApplicationDbContext> dbFactory) =>
        {
            var owner = ResolveOwner(http, http.Request.Query["userId"].ToString());
            if (owner is null) return Results.Json(new { message = "Not allowed." }, statusCode: 403);
            await using var db = await dbFactory.CreateDbContextAsync();
            var link = await db.CrmLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == owner);
            return Results.Ok(new
            {
                central = sf.CentralReady,
                connected = sf.CentralReady || link is not null,
                accountId = link?.SfAccountId,
                contactId = link?.SfContactId,
                syncedAt = link?.SyncedAt,
                newsletter = link?.NewsletterOptIn,
                instanceUrl = link?.InstanceUrl,
            });
        });
    }

    public record CrmSyncRequest(bool NewsletterOptIn, string? Interests, string? Notes, string? OwnerUserId);
}
