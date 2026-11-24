// TC001 Max Thermal Webcam: the decisions of the tray icon's update check (pure code; the self-test
// checks them). The network and the download are in src\TC001MaxWebcam.Tray\Updates.cs; installing
// is "TC001MaxWebcam update" (src\TC001MaxWebcam\Updater.cs, one administrator prompt).
//
// Where releases come from: this repository's GitHub releases, which .github\workflows\release.yml
// publishes as tag v<version> with the installer TC001MaxWebcam-<version>-x64.msi attached. The tray
// asks GitHub's REST API for the latest release (GET /repos/{owner}/{repo}/releases/latest, documented
// at https://docs.github.com/en/rest/releases/releases; no account, no personal data) at most once a
// day, and only while the update_check setting is on. A release counts only if:
//   - it is not a draft or a pre-release, and its tag is v<version>;
//   - it has exactly the asset TC001MaxWebcam-<version>-x64.msi, served from this repository's
//     releases/download/ address over HTTPS;
//   - GitHub lists that asset's SHA-256 (the "digest" field, "sha256:<hex>"); the download must match
//     it and the listed size. "TC001MaxWebcam update" then checks the package's UpgradeCode and
//     version again before it installs.
// Origin: written for this project (2026-11-23), from GitHub's REST API documentation.

using System;
using System.Globalization;
using System.Text.Json;

namespace TC001MaxWebcam.Core
{
    /// <summary>A release that can be installed: its version, the installer's name, address, size and SHA-256 (lower-case hex).</summary>
    public sealed record ReleaseInfo(Version Version, string Tag, string AssetName, string DownloadUrl, long Size, string Sha256, string PageUrl);

    public static class ReleaseCheck
    {
        public const string Repository = "MMImodular/TC001-Max-Webcam";
        public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
        public const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

        /// <summary>How often the tray asks, at most.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        /// <summary>After a failed check (offline, GitHub down), try again after this long.</summary>
        public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

        /// <summary>The installer's file name for <paramref name="v"/> (the wixproj's OutputName).</summary>
        public static string AssetName(Version v) => $"TC001MaxWebcam-{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}-x64.msi";

        /// <summary>
        /// Reads GitHub's "latest release" JSON. Returns the release, or null with <paramref name="why"/>
        /// saying why it cannot be used (a draft, no installer, no checksum, ...).
        /// </summary>
        public static ReleaseInfo? Parse(string json, out string why)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object)
                    return No("not a release", out why);
                if (Bool(r, "draft") || Bool(r, "prerelease"))
                    return No("the latest release is a draft or a pre-release", out why);
                var tag = Str(r, "tag_name") ?? "";
                if (!tag.StartsWith('v') || UpdateRules.ParseVersion(tag[1..]) is not { } version)
                    return No($"tag '{tag}' is not v<version>", out why);
                var page = Str(r, "html_url") ?? ReleasesPage;
                if (!page.StartsWith("https://github.com/" + Repository + "/", StringComparison.Ordinal))
                    page = ReleasesPage;
                var name = AssetName(version);
                if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return No("the release has no files", out why);
                foreach (var a in assets.EnumerateArray())
                {
                    if (Str(a, "name") != name)
                        continue;
                    var url = Str(a, "browser_download_url") ?? "";
                    if (url != DownloadPrefix + Uri.EscapeDataString(tag) + "/" + name)
                        return No($"{name} is not served from {DownloadPrefix}", out why);
                    var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : -1;
                    if (size <= 0)
                        return No($"{name} has no size", out why);
                    if (Sha256FromDigest(Str(a, "digest")) is not { } sha)
                        return No($"GitHub lists no SHA-256 for {name}", out why);
                    why = "";
                    return new ReleaseInfo(version, tag, name, url, size, sha, page);
                }
                return No($"the release has no {name}", out why);
            }
            catch (JsonException e)
            {
                return No("unreadable answer from GitHub: " + e.Message, out why);
            }
        }

        /// <summary>"sha256:&lt;64 hex&gt;" -> the lower-case hex; anything else -> null.</summary>
        public static string? Sha256FromDigest(string? digest)
        {
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;
            var hex = digest[7..].Trim().ToLowerInvariant();
            if (hex.Length != 64)
                return null;
            foreach (var c in hex)
                if (!Uri.IsHexDigit(c))
                    return null;
            return hex;
        }

        /// <summary>Whether a downloaded file's SHA-256 is the one listed.</summary>
        public static bool HashMatches(string expectedHex, byte[] actual) =>
            actual.Length == 32 && string.Equals(Convert.ToHexString(actual), expectedHex, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether it is time to ask again: never asked, the clock went back, or the interval
        /// (or, after a failure, the retry time) has passed.</summary>
        public static bool Due(DateTime? lastUtc, bool lastFailed, DateTime nowUtc)
        {
            if (lastUtc is not { } last || last > nowUtc)
                return true;
            return nowUtc - last >= (lastFailed ? RetryAfterFailure : Interval);
        }

        /// <summary>Whether to show a notification for <paramref name="release"/>: once per version.</summary>
        public static bool ShouldNotify(ReleaseInfo release, string? notifiedVersion) =>
            UpdateRules.ParseVersion(notifiedVersion) is not { } seen || release.Version > seen;

        public static string Format(Version v) => string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}");

        private static ReleaseInfo? No(string reason, out string why)
        {
            why = reason;
            return null;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
