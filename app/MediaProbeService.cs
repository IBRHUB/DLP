using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed record MediaProbeResult(
    string Title,
    string MediaType,
    TimeSpan? Duration,
    IReadOnlyList<int> VideoHeights);

internal static class MediaProbeService
{
    public static async Task<MediaProbeResult> ProbeAsync(
        string ytDlpPath,
        string url,
        string? referer,
        string? userAgent,
        string? cookieBrowser,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("--dump-single-json");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-warnings");
        YtDlpNetworkArgumentBuilder.AddNetworkArguments(startInfo, referer, userAgent);
        YtDlpCookieArgumentBuilder.AddCookieArguments(startInfo, cookieBrowser, url);
        startInfo.ArgumentList.Add(url);

        using Process process = new() { StartInfo = startInfo };

        try
        {
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(ShortenError(error));
            }

            return Parse(output);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("Link details timed out");
        }
    }

    private static MediaProbeResult Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string title = ReadString(root, "title") ?? "Untitled";
        TimeSpan? duration = ReadDuration(root);
        List<int> heights = [];
        bool hasVideo = false;

        if (root.TryGetProperty("formats", out JsonElement formats)
            && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement format in formats.EnumerateArray())
            {
                string? videoCodec = ReadString(format, "vcodec");
                bool isVideo = !string.IsNullOrWhiteSpace(videoCodec)
                    && !string.Equals(videoCodec, "none", StringComparison.OrdinalIgnoreCase);

                if (!isVideo)
                {
                    continue;
                }

                hasVideo = true;

                if (format.TryGetProperty("height", out JsonElement heightElement)
                    && heightElement.TryGetInt32(out int height)
                    && height > 0)
                {
                    heights.Add(height);
                }
            }
        }

        string mediaType = hasVideo ? "Video" : "Audio";
        IReadOnlyList<int> distinctHeights = heights
            .Distinct()
            .OrderDescending()
            .Take(8)
            .ToArray();

        return new MediaProbeResult(title, mediaType, duration, distinctHeights);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static TimeSpan? ReadDuration(JsonElement root)
    {
        if (!root.TryGetProperty("duration", out JsonElement durationElement))
        {
            return null;
        }

        double seconds = durationElement.ValueKind switch
        {
            JsonValueKind.Number when durationElement.TryGetDouble(out double value) => value,
            _ => 0
        };

        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static string ShortenError(string error)
    {
        string clean = YtDlpRunDiagnostics.CleanLine(error);

        if (string.IsNullOrWhiteSpace(clean))
        {
            return "Could not read link details";
        }

        return clean.Length <= 180 ? clean : clean[..180].Trim() + " ...";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Probe cleanup must never break the app window.
        }
    }
}
