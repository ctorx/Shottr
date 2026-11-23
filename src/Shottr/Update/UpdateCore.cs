using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Shottr.Update;

/// <summary>One downloadable file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>The parts of a GitHub release that the updater uses.</summary>
public sealed record ReleaseInfo(string Tag, string PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The tag without its leading "v", so it reads like the app's own version.</summary>
    public string Version => UpdateCore.StripVersionPrefix(Tag);
}

/// <summary>
/// The update logic that does no network or file work, so it can be unit tested.
/// </summary>
public static class UpdateCore
{
    /// <summary>The name of the checksum file that the release script attaches to every release.</summary>
    public const string ChecksumFileName = "SHA256SUMS";

    // ---- Release JSON -----------------------------------------------------------------------

    /// <summary>
    /// Reads the response of GitHub's "latest release" endpoint. Returns false with a readable
    /// error when the body is not JSON or has no tag_name, such as {"message":"Not Found"}.
    /// Asset entries that lack a name, URL or size are skipped.
    /// </summary>
    public static bool TryParseLatestRelease(string json, out ReleaseInfo? release, out string? error)
    {
        release = null;
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "GitHub's answer was not valid JSON: " + ex.Message;
            return false;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "GitHub's answer was not a release.";
                return false;
            }

            if (!root.TryGetProperty("tag_name", out JsonElement tagEl)
                || tagEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tagEl.GetString()))
            {
                error = root.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String
                    ? $"GitHub did not return a release. It said: {msg.GetString()}"
                    : "GitHub's answer has no tag_name, so it is not a release.";
                return false;
            }

            string pageUrl = root.TryGetProperty("html_url", out JsonElement urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()!
                : "";

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assetsEl.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!a.TryGetProperty("name", out JsonElement n) || n.ValueKind != JsonValueKind.String)
                        continue;
                    if (!a.TryGetProperty("browser_download_url", out JsonElement u) || u.ValueKind != JsonValueKind.String)
                        continue;
                    // TryGetInt64 throws on a non-number, so check the kind first.
                    if (!a.TryGetProperty("size", out JsonElement s) || s.ValueKind != JsonValueKind.Number
                        || !s.TryGetInt64(out long size) || size < 0)
                        continue;
                    assets.Add(new ReleaseAsset(n.GetString()!, u.GetString()!, size));
                }
            }

            release = new ReleaseInfo(tagEl.GetString()!.Trim(), pageUrl, assets);
            return true;
        }
    }

    // ---- Asset selection --------------------------------------------------------------------

    /// <summary>
    /// The release file name for a platform and CPU architecture, or null when Shottr has no
    /// build for it. Shottr ships one self-contained exe for 64-bit Windows.
    /// </summary>
    public static string? ExpectedAssetName(string platform, string arch) =>
        (platform.ToLowerInvariant(), arch.ToLowerInvariant()) switch
        {
            ("windows", "x64") => "Shottr.exe",
            _ => null,
        };

    /// <summary>Returns the release's file for this platform and architecture, or null.</summary>
    public static ReleaseAsset? SelectAsset(ReleaseInfo release, string platform, string arch)
    {
        string? expected = ExpectedAssetName(platform, arch);
        if (expected == null)
            return null;
        foreach (ReleaseAsset a in release.Assets)
        {
            // Windows file names are case-insensitive.
            if (string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return null;
    }

    // ---- Versions ---------------------------------------------------------------------------

    /// <summary>Removes surrounding spaces and one leading "v" or "V".</summary>
    public static string StripVersionPrefix(string version)
    {
        string v = version.Trim();
        return v.Length > 0 && (v[0] == 'v' || v[0] == 'V') ? v[1..] : v;
    }

    /// <summary>
    /// Compares two versions such as "v1.10.0" and "1.9". Returns -1 when a is older, 0 when
    /// they are equal and 1 when a is newer. A leading "v" and any "-suffix" or "+suffix" are
    /// ignored. Dotted parts compare as integers, and missing parts count as 0.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        long[] pa = VersionParts(a), pb = VersionParts(b);
        int count = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < count; i++)
        {
            long x = i < pa.Length ? pa[i] : 0;
            long y = i < pb.Length ? pb[i] : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }
        return 0;
    }

    private static long[] VersionParts(string version)
    {
        string v = StripVersionPrefix(version);
        int cut = v.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
            v = v[..cut];
        if (v.Length == 0)
            return Array.Empty<long>();
        string[] parts = v.Split('.');
        var result = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            // A part that is not a number counts as 0 rather than failing the whole comparison.
            result[i] = long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : 0;
        }
        return result;
    }

    // ---- Checksums --------------------------------------------------------------------------

    /// <summary>
    /// Finds a file's SHA-256 in a SHA256SUMS file, whose lines read "&lt;hash&gt;  &lt;filename&gt;".
    /// A "*" before the file name (sha256sum's binary mode) is allowed. Returns the hash in
    /// lowercase, or null when the file is not listed or its hash is malformed.
    /// </summary>
    public static string? ParseChecksums(string text, string filename)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space < 0)
                continue;
            string hash = line[..space];
            string name = line[(space + 1)..].TrimStart(' ', '\t');
            if (name.StartsWith('*'))
                name = name[1..];
            if (!string.Equals(name, filename, StringComparison.OrdinalIgnoreCase))
                continue;
            return IsSha256Hex(hash) ? hash.ToLowerInvariant() : null;
        }
        return null;
    }

    private static bool IsSha256Hex(string s)
    {
        if (s.Length != 64)
            return false;
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}
