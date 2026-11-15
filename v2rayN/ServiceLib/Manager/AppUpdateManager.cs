using System.IO.Compression;
using System.Reflection;

namespace ServiceLib.Manager;

/// <summary>
/// Self-update from the project's GitHub Releases. The repo and the Detour version are baked into the
/// entry assembly at build time (AssemblyMetadata DetourRepo / DetourVersion, set by CI from the tag), so a
/// local build without them never offers updates. The release zip is the same one users download by hand:
/// only the app files are replaced; bin (cores, zapret, geo files) and the user's settings stay as they are.
/// A running exe can't be overwritten but can be renamed, so locked files are moved to *.old and removed
/// on the next start.
/// </summary>
public sealed class AppUpdateManager
{
    private static readonly Lazy<AppUpdateManager> _instance = new(() => new());
    public static AppUpdateManager Instance => _instance.Value;
    private const string _tag = "AppUpdateManager";
    private const string OldSuffix = ".old";

    /// <summary>Top-level folders of the app dir the updater never touches.</summary>
    private static readonly string[] KeepDirs = ["bin", "guiConfigs", "guiLogs", "guiTemps", "guiBackups", "binConfigs"];

    public record Release(string Tag, string ZipUrl, string PageUrl);

    public static string? Repo => Meta("DetourRepo");

    public static string CurrentVersion => Meta("DetourVersion") ?? "0.0.0";

    public static bool IsConfigured => Repo.IsNotEmpty();

    /// <summary>Set by the background check at start; the Help page offers it.</summary>
    public Release? Available { get; set; }

    private static string? Meta(string key)
    {
        try
        {
            var value = Assembly.GetEntryAssembly()?
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value;
            return value.IsNullOrEmpty() ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"v1.2.3", "1.2", "v1.2.3-beta" → comparable Version; null if it isn't a version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (text.IsNullOrEmpty())
        {
            return null;
        }
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => !int.TryParse(p, out _)))
        {
            return null;
        }
        var nums = parts.Select(int.Parse).Concat([0, 0, 0]).Take(3).ToArray();
        return new Version(nums[0], nums[1], nums[2]);
    }

    public static bool IsNewer(string? tag, string? current)
    {
        var a = ParseVersion(tag);
        var b = ParseVersion(current) ?? new Version(0, 0, 0);
        return a is not null && a > b;
    }

    /// <summary>The latest release if it is newer than this build; null otherwise (or when GitHub is unreachable).</summary>
    public async Task<Release?> CheckAsync(DownloadService downloadService, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }
        var page = $"https://github.com/{Repo}/releases/latest";
        var location = await downloadService.ResolveFinalUrlAsync(page, cancellationToken);
        var tag = ZapretManager.ParseReleaseTag(location);
        Logging.SaveLog($"{_tag}: current {CurrentVersion}, latest {tag ?? "?"}");
        if (tag is null || !IsNewer(tag, CurrentVersion))
        {
            return null;
        }
        return new Release(tag,
            $"https://github.com/{Repo}/releases/download/{tag}/Detour-{tag}-win-x64.zip",
            $"https://github.com/{Repo}/releases/tag/{tag}");
    }

    /// <summary>Downloads and installs the release, then restarts the app. Returns an error message on failure.</summary>
    public async Task<string?> InstallAndRestartAsync(DownloadService downloadService, Release release,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        var zipPath = Utils.GetTempPath($"detour-update-{Utils.GetGuid()}.zip");
        var stagingDir = Utils.GetTempPath($"detour-update-{Utils.GetGuid()}");
        try
        {
            if (progress != null)
            {
                await progress($"Downloading {release.Tag}");
            }
            string? downloadError = null;
            downloadService.Error += (_, e) => downloadError ??= e.GetException().Message;
            await downloadService.DownloadFileAsync(new() { FileUrl = release.ZipUrl, FilePath = zipPath }, cancellationToken);
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            {
                return $"download failed: {downloadError ?? release.ZipUrl}";
            }

            if (progress != null)
            {
                await progress("Unpacking");
            }
            ZipFile.ExtractToDirectory(zipPath, stagingDir);
            var exeName = Path.GetFileName(Utils.GetExePath());
            var packageRoot = FindPackageRoot(stagingDir, exeName);
            if (packageRoot is null)
            {
                return $"{exeName} not found in the release archive";
            }

            var error = ReplaceAppFiles(packageRoot, Utils.StartupPath());
            if (error != null)
            {
                return error;
            }

            Logging.SaveLog($"{_tag}: installed {release.Tag}, restarting");
            ProcUtils.RebootAsAdmin(false);
            await AppManager.Instance.AppExitAsync(true);
            return null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, true);
                }
            }
            catch
            {
                // temp leftovers are harmless
            }
        }
    }

    /// <summary>The folder inside the extracted zip that holds the exe (zips have a "Detour" top folder).</summary>
    public static string? FindPackageRoot(string dir, string exeName)
    {
        if (File.Exists(Path.Combine(dir, exeName)))
        {
            return dir;
        }
        return Directory.EnumerateFiles(dir, exeName, SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();
    }

    /// <summary>
    /// Copies the package over the app dir, skipping KeepDirs. Existing files are moved to *.old first
    /// (works for the running exe and loaded dlls); on any failure everything moved so far is put back.
    /// </summary>
    public static string? ReplaceAppFiles(string packageRoot, string appDir)
    {
        var moved = new List<string>();
        var created = new List<string>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(packageRoot, src);
                var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (rel.Contains(Path.DirectorySeparatorChar) && KeepDirs.Contains(top, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    File.Move(target, target + OldSuffix, true);
                    moved.Add(target);
                }
                File.Copy(src, target);
                created.Add(target);
            }
            return null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            foreach (var file in created)
            {
                TryDelete(file);
            }
            foreach (var file in moved)
            {
                try
                {
                    File.Move(file + OldSuffix, file, true);
                }
                catch
                {
                    // best effort
                }
            }
            return ex.Message;
        }
    }

    /// <summary>Removes *.old files left by the previous update (call on start).</summary>
    public static void CleanupOldFiles(string? appDir = null)
    {
        appDir ??= Utils.StartupPath();
        try
        {
            foreach (var file in Directory.EnumerateFiles(appDir, "*" + OldSuffix, SearchOption.TopDirectoryOnly))
            {
                TryDelete(file);
            }
            foreach (var dir in Directory.EnumerateDirectories(appDir))
            {
                if (KeepDirs.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(dir, "*" + OldSuffix, SearchOption.AllDirectories))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // still locked: next start
        }
    }
}
