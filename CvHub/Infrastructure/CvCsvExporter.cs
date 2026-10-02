using System.Globalization;
using System.Text;
using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvHub.Infrastructure;

/// <summary>
/// CSV export of every published CV for a position, with candidate information columns
/// plus one column per position template attribute. Uses a fixed number of bulk queries
/// (no per-CV composition) and RFC 4180 escaping via <see cref="CvDisplay.CsvCell"/>.
/// </summary>
public static class CvCsvExporter
{
    private sealed record AttrRow(int AttributeId, string Name, AttributeType Type, string Category,
        bool Required, string? Section, string? Options);

    /// <summary>
    /// Builds the export, or returns null when the position does not exist.
    /// <paramref name="baseUrl"/> turns the CV Link column into absolute URLs.
    /// </summary>
    public static async Task<byte[]?> ExportAsync(ApplicationDbContext db, int positionId,
        string? baseUrl, CancellationToken ct = default)
    {
        var positionExists = await db.Positions.AsNoTracking().AnyAsync(p => p.Id == positionId, ct);
        if (!positionExists) return null;

        // 1) Published CVs only — candidates whose CV is still a draft are not exported.
        var cvs = await db.Cvs.AsNoTracking()
            .Where(c => c.PositionId == positionId && c.Status == CvStatus.Published)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.UserId, c.Status, c.CreatedAt, c.PublishedAt })
            .ToListAsync(ct);

        // 2) Candidate info for those CVs.
        var userIds = cvs.Select(c => c.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.UserName, u.Email })
            .ToDictionaryAsync(u => u.Id, ct);

        // 3) Template attributes define the columns (same order as the CV editor).
        var attrs = await db.PositionAttributes.AsNoTracking()
            .Where(pa => pa.PositionId == positionId)
            .OrderBy(pa => pa.SortOrder)
            .Select(pa => new AttrRow(pa.AttributeId, pa.Attribute.Name, pa.Attribute.Type,
                pa.Attribute.Category, pa.Required, pa.Section, pa.Attribute.Options))
            .ToListAsync(ct);
        var attrIds = attrs.Select(a => a.AttributeId).ToList();

        // 4) The candidates' values for those attributes, keyed by (user, attribute).
        var valueMap = (await db.AttributeValues.AsNoTracking()
                .Where(v => userIds.Contains(v.UserId) && attrIds.Contains(v.AttributeId))
                .ToListAsync(ct))
            .GroupBy(v => (v.UserId, v.AttributeId))
            .ToDictionary(g => g.Key, g => g.First());

        // 5) How many projects each CV includes.
        var projectCounts = (await db.CvProjects.AsNoTracking()
                .Where(cp => cvs.Select(c => c.Id).Contains(cp.CvId))
                .Select(cp => cp.CvId)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var sb = new StringBuilder();
        sb.Append('\uFEFF'); // UTF-8 BOM so Excel opens accents/emoji correctly.

        var header = new List<string> { "CV Id", "Candidate", "Email", "Projects", "Created", "Published", "CV Link" };
        header.AddRange(attrs.Select(a => CvDisplay.CsvCell(a.Name)));
        AppendRow(sb, header);

        foreach (var cv in cvs)
        {
            users.TryGetValue(cv.UserId, out var u);
            var candidate = u is null
                ? $"user-{cv.UserId}"
                : u.DisplayName ?? u.UserName ?? u.Email ?? $"user-{cv.UserId}";

            var cells = new List<string>
            {
                cv.Id.ToString(CultureInfo.InvariantCulture),
                candidate,
                u?.Email ?? "",
                projectCounts.GetValueOrDefault(cv.Id).ToString(CultureInfo.InvariantCulture),
                cv.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                cv.PublishedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                baseUrl is null ? $"cv/{cv.Id}" : $"{baseUrl}/cv/{cv.Id}",
            };

            foreach (var a in attrs)
            {
                valueMap.TryGetValue((cv.UserId, a.AttributeId), out var v);
                var field = ToField(a, v);
                cells.Add(CvDisplay.CsvCell(field.IsEmpty ? "" : CvDisplay.FieldValue(field)));
            }
            AppendRow(sb, cells);
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static CvFieldDto ToField(AttrRow a, AttributeValue? v) => new(
        a.AttributeId, a.Name, a.Type, a.Category, a.Required, a.Section,
        v is null || CvComposer.IsEmpty(v, a.Type),
        v?.StringValue, v?.TextValue, v?.ImageUrl, v?.NumericValue,
        v?.DateValue, v?.PeriodStart, v?.PeriodEnd,
        v?.BoolValue ?? false, v?.OptionValue,
        (a.Options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        null);

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells) =>
        sb.Append(string.Join(',', cells)).Append("\r\n"); // RFC 4180: CRLF line endings.
}
