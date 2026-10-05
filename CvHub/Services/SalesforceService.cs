using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CvHub.Services;

public class SalesforceOptions
{
    public const string SectionName = "Salesforce";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string CallbackPath { get; set; } = "/signin-salesforce";
    /// <summary>"https://login.salesforce.com" for production/dev orgs, "https://test.salesforce.com" for sandbox.</summary>
    public string LoginUrl { get; set; } = "https://login.salesforce.com";
    /// <summary>
    /// Token endpoint base for the client-credentials flow. Some newer orgs (e.g. org-farm dev
    /// editions on Hyperforce) reject this grant at login.salesforce.com with "request not
    /// supported on this domain" — set this to the org's instance URL (or "auto").
    /// </summary>
    public string CentralTokenUrl { get; set; } = "auto";
    public string ApiVersion { get; set; } = "v62.0";
    /// <summary>When true, syncs go to THIS org via the client-credentials flow — CvHub users need no Salesforce account of their own.</summary>
    public bool CentralOrg { get; set; } = true;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    /// <summary>Central mode is usable when configured; the Connected App must have the Client Credentials Flow enabled.</summary>
    public bool CentralReady => CentralOrg && IsConfigured;
}

/// <summary>Extra CRM data captured in the profile sync form.</summary>
public record CrmSyncInput(bool NewsletterOptIn, string? Interests, string? Notes);

public record CrmSyncResult(bool Success, string? Error, string? AccountId, string? ContactId, string? InstanceUrl);

/// <summary>
/// Salesforce integration (Web Server OAuth flow): builds the authorize URL, exchanges the
/// code for tokens, and creates/updates an Account with a linked Contact carrying the
/// candidate's non-removable profile fields plus the extra form data. The refresh token is
/// persisted on the user's CrmLink row so any later request can obtain a fresh access token.
/// </summary>
public class SalesforceService(IHttpClientFactory httpFactory, IOptions<SalesforceOptions> options)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public sealed record TokenResponse(
        string? access_token, string? refresh_token, string? instance_url, string? id,
        string? token_type, string? issued_at, string? signature, string? error, string? error_description);

    private SalesforceOptions O => options.Value;

    public bool IsConfigured => O.IsConfigured;
    /// <summary>True when syncs target the central org via client credentials (no per-user connect).</summary>
    public bool CentralReady => O.CentralReady;

    // PKCE verifiers pending their callback (state -> verifier). Static: the connect and
    // callback are separate requests/scopes. Entries expire after 15 minutes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Verifier, DateTimeOffset Expires)> _pkce = new();

    /// <summary>URL the user must visit to grant consent; state carries the user id (CSRF + attribution).</summary>
    public string BuildAuthorizeUrl(string baseUrl, string userId)
    {
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _pkce[state] = (verifier, DateTimeOffset.UtcNow.AddMinutes(15));
        foreach (var kv in _pkce) if (kv.Value.Expires < DateTimeOffset.UtcNow) _pkce.TryRemove(kv.Key, out _);

        return $"{O.LoginUrl}/services/oauth2/authorize" +
               $"?response_type=code" +
               $"&client_id={Uri.EscapeDataString(O.ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(baseUrl + O.CallbackPath)}" +
               $"&code_challenge={challenge}" +
               $"&code_challenge_method=S256" +
               $"&state={Uri.EscapeDataString(state + "." + userId)}";
    }

    /// <summary>Pops the PKCE verifier stored for this state (single use; null when unknown/expired).</summary>
    public string? TakeCodeVerifier(string state)
    {
        if (!_pkce.TryGetValue(state, out var entry)) return null;
        _pkce.TryRemove(state, out _);
        return entry.Expires < DateTimeOffset.UtcNow ? null : entry.Verifier;
    }

    /// <summary>Exchanges the OAuth code for tokens (called once, from the callback endpoint).</summary>
    public async Task<TokenResponse> ExchangeCodeAsync(string code, string baseUrl, string? codeVerifier)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = baseUrl + O.CallbackPath,
        };
        if (!string.IsNullOrEmpty(codeVerifier)) form["code_verifier"] = codeVerifier;
        var token = await TokenRequestAsync(form);
        if (token.refresh_token is null)
            throw new InvalidOperationException(
                "Salesforce did not return a refresh token. In the Connected App settings, enable " +
                "\"Issue JSON Web Token (JWT)-based API access\" / refresh token scope (api, refresh_token, offline_access).");
        return token;
    }

    /// <summary>Returns a fresh access token for the user, refreshing via the persisted refresh token.</summary>
    public async Task<(string AccessToken, string InstanceUrl)> GetValidTokenAsync(ApplicationDbContext db, string userId)
    {
        var link = await db.CrmLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId)
            ?? throw new InvalidOperationException("Not connected. Authorize Salesforce from the profile page first.");
        if (string.IsNullOrWhiteSpace(link.RefreshToken))
            throw new InvalidOperationException("No Salesforce refresh token stored — reconnect Salesforce.");

        var token = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = link.RefreshToken,
        });
        return (token.access_token ?? throw new InvalidOperationException("Token refresh returned no access_token."),
                link.InstanceUrl);
    }

    private async Task<TokenResponse> TokenRequestAsync(Dictionary<string, string> form)
    {
        form["client_id"] = O.ClientId;
        form["client_secret"] = O.ClientSecret;
        var http = httpFactory.CreateClient("salesforce");
        var resp = await http.PostAsync($"{O.LoginUrl}/services/oauth2/token", new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Unparseable token response from Salesforce.");
        if (token.access_token is null)
            throw new InvalidOperationException($"Salesforce token error: {token.error}: {token.error_description}");
        return token;
    }

    // Cache for the central-org token (client credentials). Static: process-wide.
    private static readonly SemaphoreSlim _centralFetch = new(1, 1);
    private static (string AccessToken, string InstanceUrl, DateTimeOffset Expires)? _centralToken;

    /// <summary>True when a cached central token is still fresh (cheap check, no lock).</summary>
    private static bool CentralTokenFresh =>
        _centralToken is { } t && t.Expires > DateTimeOffset.UtcNow;

    /// <summary>Last instance URL seen from any Salesforce token response (used by CentralTokenUrl=auto/instance).</summary>
    private static string? InstanceUrlStore { get; set; }

    private void RememberInstanceUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) InstanceUrlStore = url;
    }

    /// <summary>
    /// Access token + instance URL for the central org (client-credentials flow, cached until near expiry).
    /// CentralTokenUrl selects the token endpoint: explicit URL, "instance" (last-known org domain),
    /// or "auto" (try login.salesforce.com, then the org domain — needed because some newer
    /// org-farm/Hyperforce dev editions reject this grant at the login domain).
    /// </summary>
    public async Task<(string AccessToken, string InstanceUrl)> GetCentralTokenAsync(string? knownInstanceUrl = null)
    {
        if (CentralTokenFresh) { var c = _centralToken!.Value; return (c.AccessToken, c.InstanceUrl); }

        // Single-flight: concurrent syncs wait for one token request instead of each
        // burning a round trip to Salesforce (and racing the cache write).
        await _centralFetch.WaitAsync();
        try
        {
            if (CentralTokenFresh) { var c2 = _centralToken!.Value; return (c2.AccessToken, c2.InstanceUrl); }

            return await FetchCentralTokenAsync(knownInstanceUrl);
        }
        finally { _centralFetch.Release(); }
    }

    private async Task<(string AccessToken, string InstanceUrl)> FetchCentralTokenAsync(string? knownInstanceUrl)
    {
        RememberInstanceUrl(knownInstanceUrl);
        var bases = O.CentralTokenUrl?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => new[] { O.LoginUrl.TrimEnd('/'), InstanceUrlStore?.TrimEnd('/') }
                .Where(b => !string.IsNullOrEmpty(b)).Select(b => b!).Distinct().ToList(),
            "login" => new List<string> { O.LoginUrl.TrimEnd('/') },
            "instance" => new List<string> { string.IsNullOrWhiteSpace(InstanceUrlStore)
                ? throw new InvalidOperationException(
                    "Salesforce:CentralTokenUrl is 'instance' but no instance URL is known yet — " +
                    "complete the per-user connect once, or set CentralTokenUrl to the org's instance URL.")
                : InstanceUrlStore.TrimEnd('/') },
            _ => new List<string> { O.CentralTokenUrl.TrimEnd('/') },
        };

        var http = httpFactory.CreateClient("salesforce");
        InvalidOperationException? lastError = null;
        foreach (var tokenBase in bases)
        {
            var resp = await http.PostAsync($"{tokenBase}/services/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = O.ClientId,
                ["client_secret"] = O.ClientSecret,
            }));
            var body = await resp.Content.ReadAsStringAsync();
            var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOpts);
            if (token?.access_token is not null)
            {
                RememberInstanceUrl(token.instance_url);
                var entry = (token.access_token, token.instance_url ?? tokenBase, DateTimeOffset.UtcNow.AddMinutes(90));
                _centralToken = entry;
                return (entry.Item1, entry.Item2);
            }
            lastError = new InvalidOperationException(
                $"Salesforce central token error at {tokenBase}: {token?.error}: {token?.error_description}. " +
                "On the app's 'Edit Policies' page, enable the Client Credentials Flow and set its Run As / execution user.");
            // "request not supported on this domain" → this domain cannot serve this grant; try the next base.
            if (!string.Equals(token?.error_description, "request not supported on this domain", StringComparison.OrdinalIgnoreCase))
                throw lastError;
        }
        throw lastError ?? new InvalidOperationException("No Salesforce token endpoint configured.");
    }

/// <summary>
/// Creates (or updates, when IDs already exist) the Account + linked Contact in Salesforce
/// from the profile's non-removable fields and the sync form input. Uses the central org
/// (client credentials) when configured; otherwise the user's own connected org.
/// </summary>
public async Task<CrmSyncResult> SyncAsync(ApplicationDbContext db, string userId, CrmSyncInput input)
{
    var result = await SyncCoreAsync(db, userId, input);

    // Salesforce can invalidate a session before our assumed lifetime runs out (org session
    // policy, a password change on the Run As user, org maintenance). The cached token then
    // fails every sync until it expires on its own. Drop it, fetch a fresh one and retry once.
    if (!result.Success && MentionsInvalidSession(result.Error))
    {
        _centralToken = null;
        result = await SyncCoreAsync(db, userId, input);
    }
    return result;
}

/// <summary>Salesforce reports an expired/revoked token as INVALID_SESSION_ID with a 401.</summary>
private static bool MentionsInvalidSession(string? error) =>
    error is not null && error.Contains("INVALID_SESSION_ID", StringComparison.OrdinalIgnoreCase);

private async Task<CrmSyncResult> SyncCoreAsync(ApplicationDbContext db, string userId, CrmSyncInput input)
{
        try
        {
            string accessToken, instance;
            if (O.CentralReady)
            {
                // Pass the last-known org instance URL so 'auto' can try the org domain too.
                var savedInstance = await db.CrmLinks.AsNoTracking()
                    .Where(l => l.InstanceUrl != "").OrderByDescending(l => l.SyncedAt)
                    .Select(l => l.InstanceUrl).FirstOrDefaultAsync();
                (accessToken, instance) = await GetCentralTokenAsync(savedInstance);
            }
            else
                (accessToken, instance) = await GetValidTokenAsync(db, userId);
            var api = $"{instance}/services/data/{O.ApiVersion}";

            // --- Non-removable profile fields ---
            // These four lookups match on the attribute's NAME through a navigation, so EF
            // cannot prove the row is unique and the "first" row would otherwise be whatever
            // the planner returned. Order by AttributeId so the value is deterministic.
            var first = await db.AttributeValues.Where(v => v.UserId == userId)
                .Where(v => v.Attribute.Name == BuiltInAttributeNames.FirstName)
                .OrderBy(v => v.AttributeId)
                .Select(v => v.StringValue).FirstOrDefaultAsync();
            var last = await db.AttributeValues.Where(v => v.UserId == userId)
                .Where(v => v.Attribute.Name == BuiltInAttributeNames.LastName)
                .OrderBy(v => v.AttributeId)
                .Select(v => v.StringValue).FirstOrDefaultAsync();
            var headline = await db.AttributeValues.Where(v => v.UserId == userId)
                .Where(v => v.Attribute.Name == BuiltInAttributeNames.Headline)
                .OrderBy(v => v.AttributeId)
                .Select(v => v.StringValue).FirstOrDefaultAsync();
            var location = await db.AttributeValues.Where(v => v.UserId == userId)
                .Where(v => v.Attribute.Name == BuiltInAttributeNames.Location)
                .OrderBy(v => v.AttributeId)
                .Select(v => v.StringValue).FirstOrDefaultAsync();
            var user = await db.Users.Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.PhoneNumber, u.UserName }).FirstAsync();
            if (string.IsNullOrWhiteSpace(last))
                last = (user.Email ?? user.UserName ?? "Unknown").Split('@')[0]; // LastName is required by Salesforce

            // --- Account: the CRM "customer" bucket for this site user ---
            var accountName = string.IsNullOrWhiteSpace(first) ? last! : $"{first} {last}".Trim();
            var account = new Dictionary<string, object?>
            {
                ["Name"] = accountName,
                ["Phone"] = user.PhoneNumber,
                ["Description"] = $"CvHub user; synced {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC",
            };
            var link = await db.CrmLinks.FirstOrDefaultAsync(l => l.UserId == userId);
            string accountId;
            if (!string.IsNullOrEmpty(link?.SfAccountId))
            {
                var resp = await PatchJson($"{api}/sobjects/Account/{link.SfAccountId}", account, accessToken);
                if (!resp.IsSuccessStatusCode) return await Fail(resp, "Account update");
                accountId = link.SfAccountId;
            }
            else
            {
                var (ok, id, err) = await PostJson($"{api}/sobjects/Account", account, accessToken);
                if (!ok) return new CrmSyncResult(false, ExplainSalesforceError("Account create failed", err ?? ""), null, null, instance);
                accountId = id!;
            }

            // --- Contact: the person, linked to the Account, carrying form data ---
            var city = location?.Split(',')[0].Trim();
            var country = location?.Contains(',') == true ? location.Split(',')[1].Trim() : null;
            var contact = new Dictionary<string, object?>
            {
                ["FirstName"] = first,
                ["LastName"] = last,
                ["Email"] = user.Email,
                ["Phone"] = user.PhoneNumber,
                ["AccountId"] = accountId,
                ["LeadSource"] = "CvHub",
                ["Title"] = headline,
                ["MailingCity"] = string.IsNullOrEmpty(city) ? null : city,
                ["MailingCountry"] = string.IsNullOrEmpty(country) ? null : country,
                ["Description"] = BuildContactDescription(input),
                // Custom fields (create these in the dev org — see docs/salesforce-setup.md).
                ["Newsletter_Opt_In__c"] = input.NewsletterOptIn,
            };

            // Only send Interests when the box actually had text. Salesforce reads an
            // explicit null in a PATCH as "clear this field", and the sync dialog always
            // starts blank (it does not prefill), so always sending it would wipe a
            // previously synced value whenever someone syncs again to change something else.
            var interests = NullIfEmpty(input.Interests);
            if (interests is not null) contact["Interests__c"] = interests;

            if (!string.IsNullOrEmpty(link?.SfContactId))
            {
                var resp = await PatchJson($"{api}/sobjects/Contact/{link.SfContactId}", contact, accessToken);
                if (!resp.IsSuccessStatusCode) return await Fail(resp, "Contact update");
            }
            else
            {
                var (ok, id, err) = await PostJson($"{api}/sobjects/Contact", contact, accessToken);
                if (!ok) return new CrmSyncResult(false, ExplainSalesforceError("Contact create failed", err ?? ""), accountId, null, instance);
                link ??= new CrmLink { UserId = userId };
                link.SfContactId = id!;
            }

            link.InstanceUrl = instance;
            link.SfAccountId = accountId;
            link.NewsletterOptIn = input.NewsletterOptIn;
            link.SyncedAt = DateTimeOffset.UtcNow;
            if (link.Id == 0) db.CrmLinks.Add(link);
            await db.SaveChangesAsync();

            return new CrmSyncResult(true, null, accountId, link.SfContactId, instance);
        }
        catch (Exception ex)
        {
            return new CrmSyncResult(false, ex.Message, null, null, null);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Salesforce's INVALID_FIELD error for the two custom fields almost always means the
    /// fields were never created in the org, which is an easy mistake to make — so name it.
    /// </summary>
    private static string ExplainSalesforceError(string context, string body)
    {
        if (body.Contains("INVALID_FIELD", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Interests__c", StringComparison.OrdinalIgnoreCase))
        {
            return $"{context}: the custom fields Interests__c and Newsletter_Opt_In__c do not exist in the " +
                   "org. Create them on Contact (see docs/salesforce-setup.md step 2). Raw: " + body;
        }
        return $"{context}: {body}";
    }

    private static string BuildContactDescription(CrmSyncInput i)
    {
        var sb = new System.Text.StringBuilder("[CvHub sync]");
        if (!string.IsNullOrWhiteSpace(i.Interests)) sb.Append("\nInterests: ").Append(i.Interests.Trim());
        if (!string.IsNullOrWhiteSpace(i.Notes)) sb.Append("\nNotes: ").Append(i.Notes.Trim());
        sb.Append("\nNewsletter opt-in: ").Append(i.NewsletterOptIn ? "yes" : "no");
        return sb.ToString();
    }

    private static async Task<CrmSyncResult> Fail(HttpResponseMessage resp, string what)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var msg = body.Length <= 300 ? body : body[..300] + "…";
        return new CrmSyncResult(false,
            $"{ExplainSalesforceError($"Salesforce {what} failed ({(int)resp.StatusCode})", msg)}", null, null, null);
    }

    private async Task<HttpResponseMessage> PatchJson(string url, Dictionary<string, object?> body, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body, options: JsonOpts) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpFactory.CreateClient("salesforce").SendAsync(req);
    }

    private async Task<(bool Ok, string? Id, string? Err)> PostJson(string url, object body, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: JsonOpts) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await httpFactory.CreateClient("salesforce").SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            var msg = errBody.Length <= 300 ? errBody : errBody[..300] + "…";
            return (false, null, $"{(int)resp.StatusCode}: {msg}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (true, doc.RootElement.GetProperty("id").GetString(), null);
    }
}
