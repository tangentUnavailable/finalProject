using CvHub.Data;
using CvHub.Domain;
using CvHub.Shared;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using QuestPDF.Fluent;

namespace CvHub.Infrastructure;

/// <summary>
/// Optional requirement #1: printable PDF export of a CV, including a QR code that links
/// back to the CV page in the app. QuestPDF (Skia) renders the document; QRCoder's
/// PngByteQRCode produces the QR as a PNG without any System.Drawing dependency.
/// Pure C# on the server side (Skia bundles its own native lib in the publish output).
/// </summary>
public static class CvPdfGenerator
{
    /// <summary>Render a QR code for <paramref name="url"/> as a PNG byte[].</summary>
    private static byte[] QrPng(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(20, false);
    }

    public static async Task<byte[]> GenerateAsync(ApplicationDbContext db, Cv cv, string baseUrl)
    {
        var view = await CvComposer.ComposeAsync(db, cv);
        if (view is null)
            throw new InvalidOperationException("Unable to compose CV view for PDF export.");

        var fields = view.Fields.Where(f => !f.IsEmpty).ToList();
        var projects = (view.Projects ?? []).ToList();

        var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == cv.UserId);
        var candidate = owner is not null
            ? (owner.DisplayName ?? owner.UserName ?? owner.Email ?? $"user-{cv.UserId}")
            : $"user-{cv.UserId}";

        var qr = QrPng($"{baseUrl}/cv/{cv.Id}");

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(50);
                page.Header().Text(view.PositionTitle).SemiBold().FontSize(22);

                page.Content().Column(col =>
                {
                    col.Item().Text($"Candidate: {candidate}").FontSize(11);

                    col.Item().PaddingTop(12).Text("Profile").SemiBold().FontSize(16);
                    foreach (var f in fields)
                    {
                        col.Item().Row(row =>
                        {
                            row.ConstantItem(160f).Text(f.Name ?? "").FontSize(10).SemiBold();
                            row.RelativeItem().Text(CvDisplay.FieldValue(f)).FontSize(10);
                        });
                    }

                    if (projects.Any())
                    {
                        col.Item().PaddingTop(16).Text("Projects").SemiBold().FontSize(16);
                        foreach (var p in projects)
                        {
                            var line = string.IsNullOrWhiteSpace(p.Description)
                                ? p.Name
                                : $"{p.Name}: {p.Description}";
                            col.Item().Text($"  • {line}").FontSize(10);
                        }
                    }
                });

                page.Footer().AlignRight().Row(row =>
                {
                    row.RelativeItem().Text($"CvHub · {cv.CreatedAt:yyyy}").FontSize(8);
                    row.ConstantItem(70f).Image(qr).FitArea();
                });
            });
        }).GeneratePdf();
    }
}
