using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoidClip.Services;

// ══════════════════════════════════════════════════════════════
//  WHAT THE REPO CURRENTLY HOLDS
// ══════════════════════════════════════════════════════════════
public sealed class ReleaseInfo
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public DateTimeOffset? Published { get; set; }

    /// <summary>The downloadable build, when the release has one attached.</summary>
    public string AssetName { get; set; }
    public string AssetUrl { get; set; }
    public long AssetSize { get; set; }

    /// <summary>Version parsed out of the tag, if the tag looks like a version at all.</summary>
    public Version Version { get; set; }

    public bool HasDownload => !string.IsNullOrEmpty(AssetUrl);
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Tag : Name;
}

public sealed class UpdateCheck
{
    public bool NewerRelease { get; set; }
    public ReleaseInfo Release { get; set; }

    public int NewCommits { get; set; }
    public string LatestCommitSha { get; set; } = "";
    public string LatestCommitMessage { get; set; } = "";
    public DateTimeOffset? LatestCommitDate { get; set; }

    public string Error { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;

    public bool CanInstall => NewerRelease && Release?.HasDownload == true;
    public bool AnythingNew => NewerRelease || NewCommits > 0;

    /// <summary>One line fit for a banner or a status label.</summary>
    public string Summary
    {
        get
        {
            if (Error != null) return "Update check failed: " + Error;
            if (NewerRelease && Release != null)
                return Release.HasDownload
                    ? $"{Release.DisplayName} is available to install."
                    : $"{Release.DisplayName} was published, but it has no build attached to download.";
            if (NewCommits > 0)
                return NewCommits == 1
                    ? "1 new commit on main since this build — no release published for it yet."
                    : $"{NewCommits} new commits on main since this build — no release published for them yet.";
            return "VoidClip is up to date.";
        }
    }
}

// ══════════════════════════════════════════════════════════════
//  UPDATER
// ══════════════════════════════════════════════════════════════
/// <summary>
/// Watches the GitHub repo for new releases and commits, and can swap the running
/// single-file exe for a newer one.
/// </summary>
public static class UpdateService
{
    public const string Owner = "Frosty2904";
    public const string Repo = "Voidclips";
    public const string Branch = "main";

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";
    public static string CommitsUrl => $"{RepoUrl}/commits/{Branch}";

    private const string OldSuffix = ".old-update";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("VoidClip", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    // ── what this build is ────────────────────────────────────
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    private static string Meta(string key) => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>Commit this build was compiled from. Empty when built outside a git checkout.</summary>
    public static string BuildCommit => Meta("GitCommit") ?? "";

    public static string BuildCommitShort =>
        BuildCommit.Length >= 8 ? BuildCommit[..8] : BuildCommit;

    public static DateTimeOffset? BuildDate =>
        DateTimeOffset.TryParse(Meta("BuildDate"), out var d) ? d : null;

    public static string BuildDescription
    {
        get
        {
            var s = "Version " + CurrentVersionText;
            if (BuildCommit.Length > 0) s += "  ·  commit " + BuildCommitShort;
            if (BuildDate.HasValue) s += "  ·  built " + BuildDate.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm");
            return s;
        }
    }

    /// <summary>False when running from a dotnet-built folder rather than a published single file.</summary>
    public static bool IsSingleFileBuild
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var dll = Path.ChangeExtension(exe, ".dll");
            // a framework-dependent build sits next to its own managed dll
            return !File.Exists(dll);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  CHECK
    // ══════════════════════════════════════════════════════════
    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var result = new UpdateCheck();
        try
        {
            result.Release = await LatestReleaseAsync(ct).ConfigureAwait(false);
            if (result.Release != null) result.NewerRelease = IsNewer(result.Release);

            await FillCommitsAsync(result, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Error = Simplify(ex);
            AppPaths.Log("Update check failed: " + ex.Message);
        }
        return result;
    }

    private static string Simplify(Exception ex) => ex switch
    {
        TaskCanceledException => "timed out",
        HttpRequestException => "no connection to github.com",
        _ => ex.Message
    };

    private static async Task<ReleaseInfo> LatestReleaseAsync(CancellationToken ct)
    {
        // /releases lists newest first and includes drafts-free prereleases;
        // /releases/latest 404s on repos whose only release is a prerelease.
        var json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=10", ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;

            var info = new ReleaseInfo
            {
                Tag = Str(r, "tag_name"),
                Name = Str(r, "name"),
                Notes = Str(r, "body"),
                PageUrl = Str(r, "html_url"),
            };
            if (DateTimeOffset.TryParse(Str(r, "published_at"), out var pub)) info.Published = pub;
            info.Version = ParseVersion(info.Tag) ?? ParseVersion(info.Name);

            if (r.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name");
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    info.AssetName = name;
                    info.AssetUrl = Str(a, "browser_download_url");
                    info.AssetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    break;
                }
            }
            return info;
        }
        return null;
    }

    private static async Task FillCommitsAsync(UpdateCheck result, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/commits?sha={Branch}&per_page=1", ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return;

        result.LatestCommitSha = Str(first, "sha");
        if (first.TryGetProperty("commit", out var c))
        {
            result.LatestCommitMessage = Str(c, "message").Split('\n')[0];
            if (c.TryGetProperty("author", out var a) &&
                DateTimeOffset.TryParse(Str(a, "date"), out var when))
                result.LatestCommitDate = when;
        }

        if (BuildCommit.Length == 0) return;
        if (string.Equals(result.LatestCommitSha, BuildCommit, StringComparison.OrdinalIgnoreCase)) return;

        // how far behind this build is
        try
        {
            var cmp = await Http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/compare/{BuildCommit}...{Branch}", ct).ConfigureAwait(false);
            using var cdoc = JsonDocument.Parse(cmp);
            if (cdoc.RootElement.TryGetProperty("ahead_by", out var ahead))
                result.NewCommits = ahead.GetInt32();
        }
        catch
        {
            // the build's commit may not exist on the remote (local-only work) — the
            // latest commit info above is still worth showing
            result.NewCommits = 0;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Pulls "1.2.3" out of tags like "v1.2.3", "release-1.2", "Release 1.0".</summary>
    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;
        int P(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return new Version(P(1), P(2), P(3), P(4));
    }

    /// <summary>
    /// A release counts as newer when its tag parses to a higher version. Tags that
    /// aren't versions at all fall back to "was it published after this build".
    /// </summary>
    private static bool IsNewer(ReleaseInfo release)
    {
        if (release.Version != null)
        {
            var mine = new Version(CurrentVersion.Major, CurrentVersion.Minor,
                                   CurrentVersion.Build < 0 ? 0 : CurrentVersion.Build);
            var theirs = new Version(release.Version.Major, release.Version.Minor,
                                     release.Version.Build < 0 ? 0 : release.Version.Build);
            return theirs > mine;
        }

        if (release.Published.HasValue && BuildDate.HasValue)
            return release.Published.Value > BuildDate.Value.AddMinutes(1);

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  DOWNLOAD
    // ══════════════════════════════════════════════════════════
    /// <summary>Downloads the release build to a temp file and returns its path.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress,
                                                   CancellationToken ct = default)
    {
        if (release?.AssetUrl == null) throw new InvalidOperationException("This release has no build attached.");

        var dir = Path.Combine(Path.GetTempPath(), "VoidClipUpdate");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, release.AssetName ?? "VoidClip.exe");
        if (File.Exists(target)) File.Delete(target);

        using var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? release.AssetSize;
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(target))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
            }
        }

        Verify(target);
        return target;
    }

    /// <summary>Rejects anything that isn't a plausible Windows executable.</summary>
    private static void Verify(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1_000_000)
            throw new InvalidOperationException("The downloaded file is too small to be a VoidClip build.");

        using var fs = File.OpenRead(path);
        if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
            throw new InvalidOperationException("The downloaded file is not a Windows executable.");
    }

    // ══════════════════════════════════════════════════════════
    //  INSTALL
    // ══════════════════════════════════════════════════════════
    /// <summary>
    /// Swaps the new build in beside the running one. Windows will not let a running
    /// exe be overwritten, but it will let it be renamed — so the live file is moved
    /// aside and cleaned up on the next launch.
    /// </summary>
    public static bool Install(string downloadedExe, out string error)
    {
        error = null;
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current))
            {
                error = "Could not work out where VoidClip is running from.";
                return false;
            }

            if (!IsSingleFileBuild)
            {
                error = "This is a development build, not a published one — update it with git instead.";
                return false;
            }

            var dir = Path.GetDirectoryName(current)!;
            if (!IsWritable(dir))
            {
                error = $"“{dir}” is not writable. Move VoidClip.exe somewhere like your user folder, "
                      + "or download the update manually.";
                return false;
            }

            var parked = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(current) + OldSuffix + DateTime.Now.Ticks + ".exe");

            File.Move(current, parked);
            try
            {
                File.Copy(downloadedExe, current, overwrite: true);
            }
            catch
            {
                File.Move(parked, current);   // put things back
                throw;
            }

            AppPaths.Log($"Update installed: {Path.GetFileName(downloadedExe)} -> {current}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppPaths.Log("Update install failed: " + ex);
            return false;
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".voidclip-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Removes the previous exe left behind by an install.</summary>
    public static void CleanupOldVersions()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return;
            var dir = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(dir)) return;

            foreach (var f in Directory.EnumerateFiles(dir, "*" + OldSuffix + "*.exe"))
            {
                try { File.Delete(f); }
                catch { /* still locked — next launch will get it */ }
            }
        }
        catch { }
    }

    public static void Relaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { AppPaths.Log("Relaunch failed: " + ex.Message); }
    }

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
