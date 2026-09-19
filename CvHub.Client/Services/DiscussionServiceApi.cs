using System.Net.Http.Json;
using CvHub.Shared;

namespace CvHub.Client.Services;

/// <summary>JSON-API-backed discussion access for WebAssembly interactive rendering.</summary>
public sealed class DiscussionServiceApi(HttpClient http) : IDiscussionService
{
    public async Task<List<IDiscussionService.PostDto>> ListAsync(int positionId, bool isRecruiterView, CancellationToken ct = default)
    {
        var rows = await http.GetFromJsonAsync<List<PostRow>>($"api/positions/{positionId}/comments", ct) ?? [];
        return rows.Select(p => new IDiscussionService.PostDto(p.Id, p.Body, p.Html, p.CreatedAt, p.AuthorId, p.AuthorName, p.IsRecruiterView)).ToList();
    }

    public async Task<(bool Ok, string? Error)> PostAsync(int positionId, string body, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"api/positions/{positionId}/comments", new { body }, ct);
        if (resp.IsSuccessStatusCode) return (true, null);
        var msg = await resp.Content.ReadFromJsonAsync<ErrMsg>(cancellationToken: ct);
        return (false, msg?.Message ?? $"Post failed: {resp.StatusCode}");
    }

    private sealed record PostRow(int Id, string Body, string Html, DateTimeOffset CreatedAt, string? AuthorId, string AuthorName, bool IsRecruiterView);
    private sealed record ErrMsg(string Message);
}
