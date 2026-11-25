using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Markdig;

namespace OmpHistory.Services;

public static class MarkdownFormatHelper
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly Regex EmbeddedMarkdownBlockRegex = new(
        @"<pre><code class=""language-(?:markdown|md)"">([\s\S]*?)</code></pre>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string RenderMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "<em>(Empty response)</em>";
        }
        return RenderMarkdownPass(markdown, depth: 0);
    }

    private static string RenderMarkdownPass(string markdown, int depth)
    {
        var html = Markdown.ToHtml(markdown, Pipeline);
        if (depth >= 2)
        {
            return html;
        }

        return EmbeddedMarkdownBlockRegex.Replace(html, match =>
        {
            var innerMarkdown = WebUtility.HtmlDecode(match.Groups[1].Value);
            var innerHtml = RenderMarkdownPass(innerMarkdown, depth + 1);
            return $"<div class=\"embedded-markdown\">{innerHtml}</div>";
        });
    }

    public static string TruncatePreview(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var singleLine = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "…";
    }

    public static string FormatTimestamp(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var dto))
        {
            var local = dto.ToLocalTime();
            if (local.Date == DateTimeOffset.Now.Date)
            {
                return local.ToString("HH:mm:ss");
            }
            return local.ToString("yyyy-MM-dd HH:mm");
        }
        return iso;
    }

    public static string FormatFullDateTime(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
        return iso;
    }

    public static string FormatCredits(double? credits)
    {
        if (!credits.HasValue)
        {
            return "N/A";
        }
        return credits.Value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    public static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{(tokens / 1_000_000.0).ToString("0.1", CultureInfo.InvariantCulture)}M";
        }
        if (tokens >= 1_000)
        {
            return $"{(tokens / 1_000.0).ToString("0.1", CultureInfo.InvariantCulture)}k";
        }
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatUsageTooltip(double? credits, double? costUsd, long? totalTokens, string? model = null)
    {
        if (!credits.HasValue && !costUsd.HasValue && !totalTokens.HasValue)
        {
            return "Usage data: N/A (recorded before credit tracking)";
        }

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(model))
        {
            parts.Add($"Model: {model}");
        }
        parts.Add($"Credits: {(credits.HasValue ? credits.Value.ToString("0.##", CultureInfo.InvariantCulture) : "N/A")}");
        if (costUsd.HasValue)
        {
            parts.Add($"Est. Cost: ${costUsd.Value.ToString("0.0000", CultureInfo.InvariantCulture)}");
        }
        if (totalTokens.HasValue)
        {
            parts.Add($"Tokens: {totalTokens.Value:N0}");
        }
        return string.Join(" • ", parts);
    }
}
