using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Markdig;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CvHub.Infrastructure;

/// <summary>A finished PDF plus the name the browser should save it under.</summary>
public sealed record CvPdfExport(byte[] Bytes, string FileName);

/// <summary>Identity details that live on the user record rather than in the position template.</summary>
public sealed record CvPdfCandidate(string Name, string? Email, string? Phone);

/// <summary>
/// Optional requirement #1: a real, server-rendered PDF export of a CV. QuestPDF (Skia)
/// draws the document; QRCoder's PngByteQRCode produces the QR as a PNG without any
/// System.Drawing dependency. Pure C# on the server side (Skia bundles its own native lib
/// in the publish output), so the export does not depend on a headless browser or on the
/// client cooperating — the browser only downloads the finished file.
/// </summary>
public static class CvPdfGenerator
{
    private const string Font = "Lato";
    private const string FallbackName = "Candidate CV";

    /// <summary>
    /// The Tailwind slate ramp the web UI uses. QuestPDF ships its own named palette
    /// (no "slate"), so the hexes are spelled out here to keep the PDF consistent
    /// with the CV page the candidate is looking at.
    /// </summary>
    private static class Ink
    {
        public static readonly Color Title = Color.FromHex("#0f172a");
        public static readonly Color Body = Color.FromHex("#334155");
        public static readonly Color Muted = Color.FromHex("#475569");
        public static readonly Color Faint = Color.FromHex("#64748b");
        public static readonly Color Whisper = Color.FromHex("#94a3b8");
        public static readonly Color Rule = Color.FromHex("#e2e8f0");
        public static readonly Color Accent = Color.FromHex("#4f46e5");
    }

    private static readonly MarkdownPipeline PlainTextPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>Composes the CV, then renders it to PDF bytes.</summary>
    public static async Task<CvPdfExport> GenerateAsync(
        ApplicationDbContext db, Cv cv, string baseUrl, CancellationToken ct = default)
    {
        var view = await CvComposer.ComposeAsync(db, cv, ct)
            ?? throw new InvalidOperationException("Unable to compose CV view for PDF export.");

        var owner = await db.Users.AsNoTracking()
            .Where(u => u.Id == cv.UserId)
            .Select(u => new { u.Email, u.PhoneNumber, u.DisplayName, u.UserName })
            .FirstOrDefaultAsync(ct);

        var candidate = new CvPdfCandidate(
            FirstNonEmpty(owner?.DisplayName, owner?.UserName, $"user-{cv.UserId}"),
            owner?.Email, owner?.PhoneNumber);

        var photo = await RemoteImage.TryFetchAsync(
            HeaderImageUrl(view, BuiltInAttributeNames.Photo), ct);

        var bytes = Render(view, candidate, $"{baseUrl.TrimEnd('/')}/cv/{cv.Id}", cv.CreatedAt, photo);
        return new CvPdfExport(bytes, FileNameFor(view, candidate));
    }

    /// <summary>
    /// Draws the document. Kept separate from the data access so the layout can be rendered
    /// (and inspected) without a database.
    /// </summary>
    public static byte[] Render(
        CvViewDto view, CvPdfCandidate candidate, string cvUrl, DateTimeOffset createdAt, byte[]? photo = null)
    {
        var qr = QrPng(cvUrl);
        var accent = Ink.Accent;

        var name = Title(view, candidate);
        var headline = HeaderValue(view, BuiltInAttributeNames.Headline);
        // Location is deliberately left out of the contact line: it is a profile attribute, so
        // templates that include it already print it in the body and it would appear twice.
        var contact = string.Join("   ·   ",
            new[] { candidate.Email, candidate.Phone }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // Same exclusions as the CV page: those four are rendered in the identity block.
        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            I18n.AttributeName(BuiltInAttributeNames.Photo),
            I18n.AttributeName(BuiltInAttributeNames.FirstName),
            I18n.AttributeName(BuiltInAttributeNames.LastName),
            I18n.AttributeName(BuiltInAttributeNames.Headline),
        };

        var sections = view.Fields
            .Where(f => !f.IsEmpty && !headerNames.Contains(f.Name))
            .GroupBy(f => f.Section ?? "Details")
            .ToList();

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(45);
                page.DefaultTextStyle(x => x.FontFamily(Font).FontSize(10));

                page.Content().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        if (photo is not null) row.ConstantItem(76).Image(photo).FitArea();

                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(name).FontSize(21).SemiBold().FontColor(Ink.Title);
                            c.Item().Text(view.PositionTitle).FontSize(11.5f).FontColor(accent);
                            if (headline.Length > 0)
                                c.Item().PaddingTop(3).Text(headline).FontSize(10).FontColor(Ink.Muted);
                            if (contact.Length > 0)
                                c.Item().PaddingTop(3).Text(contact).FontSize(9).FontColor(Ink.Faint);
                        });

                        // QR sits in the top-right of the first page, mirroring the QR header
                        // the CV page shows in print mode.
                        row.ConstantItem(62).AlignTop().Image(qr).FitArea();
                    });

                    col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Ink.Rule);

                    foreach (var group in sections)
                    {
                        col.Item().PaddingTop(14).PaddingBottom(3).BorderBottom(1).BorderColor(Ink.Rule)
                            .Text(group.Key.ToUpperInvariant()).SemiBold().FontSize(9.5f).FontColor(accent);

                        foreach (var f in group)
                        {
                            if (f.Type == AttributeType.Text)
                            {
                                col.Item().Text(t => t.Span(f.Name).SemiBold().FontColor(Ink.Muted));
                                var body = PlainText(f.TextValue);
                                if (body.Length > 0) col.Item().Text(body).FontColor(Ink.Body);
                            }
                            else if (f.Type == AttributeType.Image)
                            {
                                col.Item().Text(t =>
                                {
                                    t.Span(f.Name).SemiBold().FontColor(Ink.Muted);
                                    t.Span("   ").FontColor(Ink.Muted);
                                    t.Span(f.ImageUrl ?? "").FontSize(8f).FontColor(Ink.Whisper);
                                });
                            }
                            else
                            {
                                col.Item().Text(t =>
                                {
                                    t.Span(f.Name + ":  ").SemiBold().FontColor(Ink.Muted);
                                    t.Span(CvDisplay.FieldValue(f)).FontColor(Ink.Body);
                                });
                            }
                        }
                    }

                    if (view.Projects.Count > 0)
                    {
                        col.Item().PaddingTop(14).PaddingBottom(3).BorderBottom(1).BorderColor(Ink.Rule)
                            .Text("PROJECTS").SemiBold().FontSize(9.5f).FontColor(accent);

                        foreach (var p in view.Projects)
                        {
                            var period = Period(p);
                            col.Item().PaddingTop(7).Row(row =>
                            {
                                row.RelativeItem().Text(p.Name).SemiBold();
                                if (period.Length > 0)
                                    row.ConstantItem(150).AlignRight().Text(period).FontSize(9).FontColor(Ink.Faint);
                            });

                            if (p.Tags.Length > 0)
                                col.Item().Text(string.Join("   ·   ", p.Tags)).FontSize(8.5f).FontColor(accent);

                            var description = PlainText(p.Description);
                            if (description.Length > 0) col.Item().Text(description).FontColor(Ink.Body);
                        }
                    }
                });

                page.Footer().PaddingTop(8).BorderTop(1).BorderColor(Ink.Rule).Row(row =>
                {
                    row.RelativeItem().AlignLeft().Text(t =>
                    {
                        t.Span("CvHub").SemiBold();
                        t.Span($"   ·   CV #{view.CvId}   ·   {createdAt:yyyy-MM-dd}")
                            .FontSize(8.5f).FontColor(Ink.Whisper);
                    });
                    row.RelativeItem().AlignCenter().Text(t =>
                    {
                        t.Span("Page ").FontSize(8.5f).FontColor(Ink.Whisper);
                        t.CurrentPageNumber().FontSize(8.5f).FontColor(Ink.Whisper);
                        t.Span(" of ").FontSize(8.5f).FontColor(Ink.Whisper);
                        t.TotalPages().FontSize(8.5f).FontColor(Ink.Whisper);
                    });
                });
            });
        }).GeneratePdf();
    }

    private static string FullName(CvViewDto view)
    {
        var name = $"{HeaderValue(view, BuiltInAttributeNames.FirstName)} {HeaderValue(view, BuiltInAttributeNames.LastName)}".Trim();
        return name.Length > 0 ? name : FallbackName;
    }

    /// <summary>
    /// Document title. Not every position template includes First/Last Name, so fall back
    /// to the account display name rather than printing a placeholder or an e-mail address.
    /// </summary>
    private static string Title(CvViewDto view, CvPdfCandidate candidate)
    {
        var name = FullName(view);
        if (name == FallbackName && !string.IsNullOrWhiteSpace(candidate.Name)) return candidate.Name;
        return name;
    }

    /// <summary>Value of one built-in header attribute, matched on the translated display name.</summary>
    private static string HeaderValue(CvViewDto view, string attributeName)
    {
        var target = I18n.AttributeName(attributeName);
        var field = view.Fields.FirstOrDefault(f => string.Equals(f.Name, target, StringComparison.OrdinalIgnoreCase));
        return field is null || field.IsEmpty ? "" : (field.StringValue ?? "").Trim();
    }

    private static string Period(ProjectDto p) =>
        p.Start is null && p.End is null
            ? ""
            : $"{(p.Start?.ToString("MMM yyyy") ?? "?")} — {(p.End?.ToString("MMM yyyy") ?? "present")}";

    /// <summary>
    /// URL of a built-in image attribute. Image values live in ImageUrl, not StringValue,
    /// so this cannot reuse <see cref="HeaderValue"/>.
    /// </summary>
    private static string? HeaderImageUrl(CvViewDto view, string attributeName)
    {
        var target = I18n.AttributeName(attributeName);
        var field = view.Fields.FirstOrDefault(f => string.Equals(f.Name, target, StringComparison.OrdinalIgnoreCase));
        return field is null || field.IsEmpty ? null : field.ImageUrl;
    }

    /// <summary>Markdown in field and project text becomes readable plain text on paper.</summary>
    private static string PlainText(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? "" : Markdown.ToPlainText(markdown, PlainTextPipeline).Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? FallbackName;

    private static byte[] QrPng(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(20, false);
    }

    public static string FileNameFor(CvViewDto view, CvPdfCandidate candidate)
    {
        var safe = Sanitize($"{Title(view, candidate)} - {view.PositionTitle}");
        return $"{(safe.Length > 0 ? safe : $"cv-{view.CvId}")}.pdf";
    }

    private static string Sanitize(string value)
    {
        // Path.GetInvalidFileNameChars() is only '/' and NUL on Linux; a CV title can also
        // carry Windows-illegal characters that make the download awkward on a Windows client.
        var illegal = Path.GetInvalidFileNameChars().Concat(['\\', ':', '*', '?', '"', '<', '>', '|', '/']).ToHashSet();
        var cleaned = new string(value.Select(c => illegal.Contains(c) ? '-' : c).ToArray());
        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }
}
