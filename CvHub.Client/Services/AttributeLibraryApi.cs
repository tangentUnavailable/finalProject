using System.Net.Http.Json;
using CvHub.Shared;

namespace CvHub.Client.Services;

/// <summary>JSON-API-backed attribute lookups for WebAssembly interactive rendering.</summary>
public sealed class AttributeLibraryApi(HttpClient http) : IAttributeLibrary
{
    public async Task<List<IAttributeLibrary.AttributeRow>> SearchAsync(string? prefix, string? category, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(prefix)) q.Add($"prefix={Uri.EscapeDataString(prefix)}");
        if (!string.IsNullOrEmpty(category)) q.Add($"category={Uri.EscapeDataString(category)}");
        var url = "api/attributes/search" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        return await http.GetFromJsonAsync<List<IAttributeLibrary.AttributeRow>>(url, ct) ?? [];
    }

    public async Task<List<IAttributeLibrary.AttributeRow>> RecentAsync(string? prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(prefix))
            return [];
        return await http.GetFromJsonAsync<List<IAttributeLibrary.AttributeRow>>("api/attributes/recent", ct) ?? [];
    }

    public async Task<HashSet<int>> PinnedAsync(CancellationToken ct = default)
    {
        var ids = await http.GetFromJsonAsync<List<int>>("api/attributes/pinned", ct) ?? [];
        return ids.ToHashSet();
    }
}
