namespace CvHub.Shared;

/// <summary>Discussion reads/writes for the InteractiveAuto DiscussionPanel, per host.
/// Server implementation queries the DB; WASM implementation calls the JSON API.</summary>
public interface IDiscussionService
{
    Task<List<PostDto>> ListAsync(int positionId, bool isRecruiterView, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> PostAsync(int positionId, string body, CancellationToken ct = default);

    public sealed record PostDto(
        int Id, string Body, string Html, DateTimeOffset CreatedAt,
        string? AuthorId, string AuthorName, bool IsRecruiterView);
}
