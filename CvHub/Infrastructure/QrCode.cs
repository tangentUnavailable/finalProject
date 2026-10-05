using QRCoder;

namespace CvHub.Infrastructure;

/// <summary>QR-code generation for the printable CV view (links back to the app).</summary>
public static class Qr
{
    /// <summary>Renders the URL as a base64 PNG data URL (no external services, no System.Drawing).</summary>
    public static string DataUrl(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        return "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(4));
    }
}
