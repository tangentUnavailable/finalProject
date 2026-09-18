using Ganss.Xss;
using Markdig;

namespace CvHub.Infrastructure;

/// <summary>
/// Server-side Markdown rendering (Markdig) with HTML sanitization (HtmlSanitizer),
/// so rendered content is safe during static SSR — no JS interop needed.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = new();

    public static string Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var html = Markdown.ToHtml(markdown, Pipeline);
        return Sanitizer.Sanitize(html);
    }
}
