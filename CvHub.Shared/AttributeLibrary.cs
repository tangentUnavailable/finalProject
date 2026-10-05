namespace CvHub.Shared;

/// <summary>Attribute-library lookups for the attribute picker, per host.
/// Server implementation queries the DB; WASM implementation calls the JSON API.</summary>
public interface IAttributeLibrary
{
    Task<List<AttributeRow>> SearchAsync(string? prefix, string? category, CancellationToken ct = default);

    Task<List<AttributeRow>> RecentAsync(string? prefix, CancellationToken ct = default);

    Task<HashSet<int>> PinnedAsync(CancellationToken ct = default);

    public sealed record AttributeRow(int Id, string Name, string Category, AttributeType Type);
}
