using System.Net;
using System.Net.Sockets;

namespace CvHub.Infrastructure;

/// <summary>
/// Downloads the profile photo for the PDF export. The URL is user-supplied (the uploader
/// posts a Cloudinary URL), so this must never become an open proxy: only http(s), no
/// redirects, every resolved address must be public, and the body is capped. Any failure
/// returns null and the document is simply rendered without a photo.
/// </summary>
public static class RemoteImage
{
    private const long MaxBytes = 5 * 1024 * 1024;

    // Static client with redirects disabled: a 302 to http://169.254.169.254/ would slip
    // past the address check below, so a redirect is treated as a failure instead.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public static async Task<byte[]?> TryFetchAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); }
        catch { return null; }
        if (addresses.Length == 0 || addresses.Any(IsPrivate)) return null;

        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;
            if (response.Content.Headers.ContentLength > MaxBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            // Content-Length is advisory, so cap what actually arrived too.
            return buffer.Length > MaxBytes || buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>Loopback, link-local (cloud metadata), private, multicast and reserved ranges.</summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                  // 0.0.0.0/8 "this network"
                10 => true,                                 // 10/8 private
                127 => true,                                // loopback
                169 when b[1] == 254 => true,               // 169.254/16 link-local / metadata
                172 when b[1] >= 16 && b[1] <= 31 => true,  // 172.16/12 private
                192 when b[1] == 168 => true,               // 192.168/16 private
                100 when b[1] >= 64 && b[1] <= 127 => true, // 100.64/10 CGNAT
                >= 224 => true,                             // multicast + reserved
                _ => false,
            };
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast || address.IsIPv6Teredo
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC); // fc00::/7 unique-local
    }
}
