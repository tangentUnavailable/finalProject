using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CvHub.Data;
using CvHub.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CvHub.Services;

public class DropboxOptions
{
    public const string SectionName = "Dropbox";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Long-lived offline refresh token produced by the one-time authorization-code flow.</summary>
    public string RefreshToken { get; set; } = "";
    /// <summary>Folder inside the Dropbox app's root where ticket JSON files land (Power Automate watches this).</summary>
    public string Folder { get; set; } = "/cvhub-support-tickets";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RefreshToken);
}

public record SupportTicketInput(string Summary, string Priority);

public record SupportTicketResult(bool Success, string? Error, string? FileName, string? JsonPreview);

/// <summary>
/// Support-ticket export used by the Power Automate integration: builds a JSON file with
/// reported-by (user + role), optional position title, originating page link, user-chosen
/// priority and the admins' e-mail addresses, then uploads it to Dropbox. A cloud flow
/// picks the new file up and e-mails the admins (see docs/power-automate-flow.md).
/// </summary>
public class SupportTicketService(
    IHttpClientFactory httpFactory,
    IOptions<DropboxOptions> options,
    UserManager<ApplicationUser> userManager,
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The only priorities the flow's e-mail subject template is written for.</summary>
    private static readonly string[] AllowedPriorities = ["High", "Average", "Low"];
    private const int MaxSummaryLength = 2000;

    /// <summary>
    /// Uploads one file per call into a watched Dropbox folder, and the dialog is reachable
    /// from every page, so a single user could otherwise flood it. Static because the
    /// service is scoped to a circuit, which lives far longer than the throttle window.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _uploads = new();
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(10);
    private const int MaxUploadsPerWindow = 5;

    private DropboxOptions O => options.Value;

    // Short-lived access tokens are cached for the lifetime of this scoped service.
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    public bool IsConfigured => O.IsConfigured;

    /// <summary>
    /// Builds and uploads the ticket. Returns a friendly error string on failure so the
    /// dialog can show it inline.
    /// </summary>
    public async Task<SupportTicketResult> CreateAndUploadAsync(SupportTicketInput input, string pageUrl, int? positionId, ClaimsPrincipal principal)
    {
        if (!IsConfigured)
            return new SupportTicketResult(false, "Dropbox upload is not configured on the server.", null, null);

        // The dialog offers a fixed set of priorities, but the value travels over the
        // Blazor circuit and must not be trusted: it ends up in the admins' e-mail subject.
        if (!AllowedPriorities.Contains(input.Priority))
            return new SupportTicketResult(false, $"Priority must be one of: {string.Join(", ", AllowedPriorities)}.", null, null);
        if (string.IsNullOrWhiteSpace(input.Summary))
            return new SupportTicketResult(false, "Summary is required.", null, null);
        if (input.Summary.Length > MaxSummaryLength)
            return new SupportTicketResult(false, $"Summary must be {MaxSummaryLength} characters or fewer.", null, null);

        try
        {
            var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var user = userId is null ? null : await userManager.FindByIdAsync(userId);
            if (user is null)
                return new SupportTicketResult(false, "You need to be signed in to create a support ticket.", null, null);

            var role = await ResolveRoleAsync(user);
            var adminEmails = (await userManager.GetUsersInRoleAsync(IdentitySeeder.Roles.Admin))
                .Select(u => u.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? positionTitle = null;
            if (positionId is int pid)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                positionTitle = await db.Positions.AsNoTracking()
                    .Where(p => p.Id == pid)
                    .Select(p => p.Title)
                    .FirstOrDefaultAsync();
            }

            var ticket = new Dictionary<string, object?>
            {
                // Field names follow the assignment spec ("Reported by", "Position", "Link",
                // "Priority") in snake_case so Power Automate expressions stay simple.
                ["reported_by"] = $"{user.DisplayName ?? user.Email} ({role})",
                ["reported_by_email"] = user.Email,
                ["position"] = positionTitle,
                ["link"] = pageUrl,
                ["priority"] = input.Priority,
                ["summary"] = input.Summary,
                ["admin_emails"] = adminEmails,
                ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["application"] = "CvHub",
            };

            var json = JsonSerializer.Serialize(ticket, JsonOpts);
            var fileName = $"support-ticket-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json";

            // Counted only once the ticket is actually accepted, so a rejected submit
            // (not configured, signed out, bad input) never burns the user's quota.
            if (!TryRecordUpload(user.Id))
                return new SupportTicketResult(false,
                    $"You have created {MaxUploadsPerWindow} support tickets in the last 10 minutes. Please wait before sending more.",
                    null, null);

            await UploadAsync(fileName, json);

            return new SupportTicketResult(true, null, fileName, json);
        }
        catch (Exception ex)
        {
            return new SupportTicketResult(false, ex.Message, null, null);
        }
    }

    /// <summary>Sliding-window throttle. Returns false once the user exceeds the limit.</summary>
    private static bool TryRecordUpload(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        var queue = _uploads.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > UploadWindow) queue.Dequeue();
            if (queue.Count >= MaxUploadsPerWindow) return false;
            queue.Enqueue(now);
        }
        if (_uploads.Count > 5000) _uploads.Clear(); // bounded memory; cheap to rebuild
        return true;
    }

    private async Task<string> ResolveRoleAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        foreach (var r in new[] { IdentitySeeder.Roles.Admin, IdentitySeeder.Roles.Recruiter, IdentitySeeder.Roles.Candidate })
            if (roles.Contains(r)) return r;
        return roles.FirstOrDefault() ?? "User";
    }

    private async Task UploadAsync(string fileName, string json)
    {
        var token = await GetAccessTokenAsync();
        var http = httpFactory.CreateClient("dropbox");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload")
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json)),
        };
        // The API-arg header must be JSON; the path stays ASCII (folder + generated name) so
        // no header-escaping surprises are possible.
        var arg = JsonSerializer.Serialize(new
        {
            path = $"{O.Folder.TrimEnd('/')}/{fileName}",
            mode = "add",
            autorename = false,
            mute = false,
        });
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Dropbox upload failed ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }
    }

    private async Task<string> GetAccessTokenAsync()
    {
        if (_accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return _accessToken;

        var http = httpFactory.CreateClient("dropbox");
        using var response = await http.PostAsync("https://api.dropboxapi.com/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = O.RefreshToken,
                ["client_id"] = O.ClientId,
                ["client_secret"] = O.ClientSecret,
            }));

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Could not refresh Dropbox token ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        _accessToken = payload.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Dropbox returned no access token.");
        var expiresIn = payload.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 14400;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return _accessToken;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
