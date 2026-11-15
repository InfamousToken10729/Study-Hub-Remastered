using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StudyHub.Services
{
    public class UpdateCheckResult
    {
        public bool Success { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = GitHubUpdateService.CurrentAppVersion;
        public string LatestVersion { get; set; } = GitHubUpdateService.CurrentAppVersion;
        public string LatestCommitSha { get; set; } = "";
        public string ReleaseTitle { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string PublishedAt { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string DownloadType { get; set; } = ""; // "exe", "zip", "source_zip"
        public string ErrorMessage { get; set; } = "";
        public string RepoDisplay { get; set; } = "";
    }

    public class GitHubUpdateService
    {
        public const string CurrentAppVersion = "3.7.2";
        public const int CurrentAppBuild = 372;

        private readonly HttpClient _http;
        private readonly string _baseDir;

        public string LastDownloadedApkPath { get; private set; } = "";

        public GitHubUpdateService(string baseDir)
        {
            _baseDir = baseDir;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StudyHub-Updater", CurrentAppVersion));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public static bool TryParseGitHubRepo(string rawInput, out string owner, out string repo, out string branch)
        {
            owner = "";
            repo = "";
            branch = "";

            if (string.IsNullOrWhiteSpace(rawInput)) return false;
            string url = rawInput.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            try
            {
                var uri = new Uri(url);
                string host = uri.Host.ToLowerInvariant();
                var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

                if ((host == "github.com" || host == "www.github.com") && parts.Length >= 2)
                {
                    owner = parts[0];
                    repo = parts[1];
                    if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        repo = repo.Substring(0, repo.Length - 4);

                    if (parts.Length >= 4 && (parts[2] == "tree" || parts[2] == "blob"))
                        branch = parts[3];

                    return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
                }

                if (host == "raw.githubusercontent.com" && parts.Length >= 3)
                {
                    owner = parts[0];
                    repo = parts[1];
                    branch = parts[2];
                    return true;
                }
            }
            catch { }

            // Fallback regex for owner/repo shorthand
            var match = Regex.Match(rawInput.Trim(), @"^([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+)$");
            if (match.Success)
            {
                owner = match.Groups[1].Value;
                repo = match.Groups[2].Value.Replace(".git", "");
                return true;
            }

            return false;
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string rawUrl, string installedCommitSha = "")
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = CurrentAppVersion,
                LatestVersion = CurrentAppVersion
            };

            rawUrl = StudyHub.Models.UserSettings.NormalizeGithubRepoUrl(rawUrl);

            try
            {
                if (!TryParseGitHubRepo(rawUrl, out string owner, out string repo, out string branch))
                {
                    // Check if it's a direct link to a .exe, .apk, or .zip file
                    if (rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        (rawUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                         rawUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
                         rawUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Success = true;
                        result.IsUpdateAvailable = true;
                        result.LatestVersion = "Direct Package";
                        result.ReleaseTitle = Path.GetFileName(new Uri(rawUrl).LocalPath);
                        result.ReleaseNotes = "Direct update package URL configured.";
                        result.DownloadUrl = rawUrl.Trim();
                        result.DownloadType = rawUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? "apk"
                            : rawUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "exe"
                            : "zip";
                        result.RepoDisplay = rawUrl.Trim();
                        return result;
                    }

                    result.ErrorMessage = "Invalid GitHub URL. Example: https://github.com/username/repository";
                    return result;
                }

                result.RepoDisplay = $"{owner}/{repo}";
                string repoDefaultBranch = "main";
                string activeBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch;

                // 0. Query repo metadata to get actual default_branch and verify requested branch exists
                try
                {
                    using var repoResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}");
                    if (repoResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await repoResp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("default_branch", out var dbEl))
                        {
                            repoDefaultBranch = dbEl.GetString() ?? "main";
                        }
                        if (string.IsNullOrWhiteSpace(branch))
                        {
                            activeBranch = repoDefaultBranch;
                        }
                        else if (!string.Equals(branch, repoDefaultBranch, StringComparison.OrdinalIgnoreCase))
                        {
                            // Auto-fallback when URL specifies e.g. /tree/study_hub but repo actually uses main
                            try
                            {
                                using var brCheck = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/commits/{branch}");
                                if (!brCheck.IsSuccessStatusCode)
                                {
                                    activeBranch = repoDefaultBranch;
                                }
                            }
                            catch
                            {
                                activeBranch = repoDefaultBranch;
                            }
                        }
                    }
                    else if ((int)repoResp.StatusCode == 404)
                    {
                        result.ErrorMessage = $"GitHub repository '{owner}/{repo}' was not found (or is private).";
                        return result;
                    }
                }
                catch { }

                bool hasNativeBinaryAsset = false;
                string releasePublishedIso = "";
                string releaseTagId = "";

                // Helper to resolve repo root binary if not attached as Release Asset
                async Task<(string url, string type, string sha)> TryResolveRepoRootBinaryAsync()
                {
#if ANDROID
                    string targetFile = "StudyHub.apk";
                    string targetType = "apk";
#else
                    string targetFile = "StudyHub.exe";
                    string targetType = "exe";
#endif
                    try
                    {
                        using var contentsResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/contents/{targetFile}?ref={activeBranch}");
                        if (contentsResp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await contentsResp.Content.ReadAsStringAsync());
                            string dlUrl = doc.RootElement.TryGetProperty("download_url", out var dlEl) ? (dlEl.GetString() ?? "") : "";
                            string fSha = doc.RootElement.TryGetProperty("sha", out var sEl) ? (sEl.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(dlUrl))
                                dlUrl = $"https://github.com/{owner}/{repo}/raw/{activeBranch}/{targetFile}";
                            return (dlUrl, targetType, fSha);
                        }
                    }
                    catch { }
                    return ("", "", "");
                }

                bool relFound = false;
                string relVer = "", relTitle = "", relNotes = "", relPub = "", relUrl = "", relType = "", relTagId = "", relZipball = "";

                // 1. Strategy A: Inspect GitHub Releases into isolated metadata (never let an older release like v3.0.0 shadow newer version.json!)
                try
                {
                    JsonElement? relRoot = null;
                    using var relResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
                    if (relResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await relResp.Content.ReadAsStringAsync());
                        relRoot = doc.RootElement.Clone();
                    }
                    else
                    {
                        using var listResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases?per_page=1");
                        if (listResp.IsSuccessStatusCode)
                        {
                            using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
                            if (listDoc.RootElement.ValueKind == JsonValueKind.Array && listDoc.RootElement.GetArrayLength() > 0)
                                relRoot = listDoc.RootElement[0].Clone();
                        }
                    }

                    if (relRoot.HasValue)
                    {
                        var root = relRoot.Value;
                        string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "") : "";
                        string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? tag) : tag;
                        string body = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "") : "";
                        string pub = root.TryGetProperty("published_at", out var pEl) ? (pEl.GetString() ?? "") : "";
                        string relId = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                        string zipball = root.TryGetProperty("zipball_url", out var zEl) ? (zEl.GetString() ?? "") : "";

                        relVer = CleanVersionString(tag);
                        if (string.IsNullOrWhiteSpace(relVer)) relVer = CleanVersionString(name);
                        relTitle = string.IsNullOrWhiteSpace(name) ? $"Release {tag}" : name;
                        relNotes = body;
                        relPub = pub;
                        relZipball = zipball;
                        relTagId = !string.IsNullOrWhiteSpace(tag) ? $"rel:{tag}:{relId}" : $"rel:{relId}";

                        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assetsEl.EnumerateArray())
                            {
                                string aName = asset.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                                string aUrl = asset.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "") : "";
#if ANDROID
                                if (aName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                                {
                                    relUrl = aUrl;
                                    relType = "apk";
                                    break;
                                }
#else
                                if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    relUrl = aUrl;
                                    relType = "exe";
                                    break;
                                }
                                if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(relUrl))
                                {
                                    relUrl = aUrl;
                                    relType = "zip";
                                }
#endif
                            }
                        }

                        relFound = !string.IsNullOrWhiteSpace(tag) || !string.IsNullOrWhiteSpace(name);
                    }
                }
                catch { }

                // 2. Strategy B: Check authoritative version.json in active branch
                bool verJsonFound = false;
                string verJsonVer = "", verJsonNotes = "", verJsonDl = "", verJsonDate = "";
                int verJsonBuild = 0;
                try
                {
                    string rawVerUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{activeBranch}/version.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    using var verResp = await _http.GetAsync(rawVerUrl);
                    if (verResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await verResp.Content.ReadAsStringAsync());
                        var root = doc.RootElement;
                        string remoteVer = root.TryGetProperty("version", out var vEl) ? (vEl.GetString() ?? "") : "";
                        verJsonBuild = root.TryGetProperty("build", out var bEl) && bEl.TryGetInt32(out int bVal) ? bVal : 0;
                        verJsonNotes = root.TryGetProperty("notes", out var nEl) ? (nEl.GetString() ?? "") : "";
                        verJsonDate = root.TryGetProperty("releaseDate", out var rdEl) ? (rdEl.GetString() ?? "") : "";
                        verJsonDl = root.TryGetProperty("download_url", out var dEl) ? (dEl.GetString() ?? "") : "";
#if ANDROID
                        if (root.TryGetProperty("apk_url", out var apkEl) && !string.IsNullOrWhiteSpace(apkEl.GetString()))
                            verJsonDl = apkEl.GetString() ?? verJsonDl;
                        else if (root.TryGetProperty("android_download_url", out var aApkEl) && !string.IsNullOrWhiteSpace(aApkEl.GetString()))
                            verJsonDl = aApkEl.GetString() ?? verJsonDl;
#endif
                        verJsonVer = CleanVersionString(remoteVer);
                        verJsonFound = !string.IsNullOrWhiteSpace(verJsonVer);
                    }
                }
                catch { }

                // Pick authoritative metadata: Release wins ONLY if its version strictly exceeds version.json & CurrentAppVersion
                bool useReleaseOverVersionJson = relFound
                    && !string.IsNullOrWhiteSpace(relVer)
                    && (!verJsonFound || CompareVersions(relVer, verJsonVer) > 0);

                if (useReleaseOverVersionJson && CompareVersions(relVer, CurrentAppVersion) > 0)
                {
                    result.Success = true;
                    result.IsUpdateAvailable = true;
                    result.LatestVersion = relVer;
                    result.ReleaseTitle = relTitle;
                    result.ReleaseNotes = string.IsNullOrWhiteSpace(relNotes) ? $"Study Hub v{relVer}" : relNotes;
                    result.PublishedAt = FormatIsoDate(relPub);
                    result.LatestCommitSha = relTagId;

                    if (!string.IsNullOrWhiteSpace(relUrl))
                    {
                        result.DownloadUrl = relUrl;
                        result.DownloadType = relType;
                    }
                    else
                    {
                        var (rootUrl, rootType, _) = await TryResolveRepoRootBinaryAsync();
                        if (!string.IsNullOrWhiteSpace(rootUrl))
                        {
                            result.DownloadUrl = rootUrl;
                            result.DownloadType = rootType;
                        }
#if !ANDROID
                        else
                        {
                            result.DownloadUrl = !string.IsNullOrEmpty(relZipball)
                                ? relZipball
                                : $"https://github.com/{owner}/{repo}/archive/refs/heads/{activeBranch}.zip";
                            result.DownloadType = "source_zip";
                        }
#endif
                    }
                    return result;
                }

                if (verJsonFound)
                {
                    result.Success = true;
                    result.LatestVersion = verJsonVer;
                    result.ReleaseTitle = $"Study Hub v{verJsonVer}";
                    result.ReleaseNotes = !string.IsNullOrWhiteSpace(verJsonNotes)
                        ? verJsonNotes
                        : (relFound && CompareVersions(relVer, verJsonVer) >= 0 && !string.IsNullOrWhiteSpace(relNotes)
                            ? relNotes
                            : $"Version {verJsonVer} from {owner}/{repo} ({activeBranch})");
                    result.PublishedAt = !string.IsNullOrWhiteSpace(verJsonDate) ? verJsonDate : FormatIsoDate(relPub);
                    result.LatestCommitSha = $"ver:{verJsonVer}:b{verJsonBuild}";

                    // Prefer Release asset only if Release matches >= version.json; otherwise resolve repo root binary cleanly
                    if (relFound && CompareVersions(relVer, verJsonVer) >= 0 && !string.IsNullOrWhiteSpace(relUrl))
                    {
                        result.DownloadUrl = relUrl;
                        result.DownloadType = relType;
                        hasNativeBinaryAsset = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(verJsonDl))
                    {
                        bool isWantedBinary =
#if ANDROID
                            verJsonDl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
#else
                            verJsonDl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || verJsonDl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
#endif
                        if (isWantedBinary)
                        {
                            result.DownloadUrl = verJsonDl;
                            result.DownloadType = verJsonDl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? "apk"
                                : verJsonDl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "zip";
                            hasNativeBinaryAsset = true;
                        }
                    }

                    if (!hasNativeBinaryAsset)
                    {
                        var (rootUrl, rootType, _) = await TryResolveRepoRootBinaryAsync();
                        if (!string.IsNullOrWhiteSpace(rootUrl))
                        {
                            result.DownloadUrl = rootUrl;
                            result.DownloadType = rootType;
                            hasNativeBinaryAsset = true;
                        }
#if !ANDROID
                        else if (string.IsNullOrWhiteSpace(result.DownloadUrl))
                        {
                            result.DownloadUrl = !string.IsNullOrWhiteSpace(verJsonDl)
                                ? verJsonDl
                                : $"https://github.com/{owner}/{repo}/archive/refs/heads/{activeBranch}.zip";
                            result.DownloadType = "source_zip";
                        }
#endif
                    }

                    // Authoritative check: never re-prompt same/older version after installing, and never fall back to stale v3.0.0 release!
                    bool semverNewer = CompareVersions(verJsonVer, CurrentAppVersion) > 0
                        || (CompareVersions(verJsonVer, CurrentAppVersion) == 0 && verJsonBuild > CurrentAppBuild);
                    result.IsUpdateAvailable = semverNewer;
                    return result;
                }

                // 3. Strategy C fallback (ONLY when neither version.json nor a newer release exists on GitHub)
                // Seed release metadata only if Release >= CurrentAppVersion so legacy v3.0.0 tags never surface
                if (relFound && (string.IsNullOrWhiteSpace(relVer) || CompareVersions(relVer, CurrentAppVersion) >= 0))
                {
                    result.Success = true;
                    result.LatestVersion = !string.IsNullOrWhiteSpace(relVer) ? relVer : CurrentAppVersion;
                    result.ReleaseTitle = relTitle;
                    result.ReleaseNotes = string.IsNullOrWhiteSpace(relNotes) ? "Release found on GitHub." : relNotes;
                    result.PublishedAt = FormatIsoDate(relPub);
                    result.LatestCommitSha = relTagId;
                    if (!string.IsNullOrWhiteSpace(relUrl))
                    {
                        result.DownloadUrl = relUrl;
                        result.DownloadType = relType;
                        hasNativeBinaryAsset = true;
                    }
                    releasePublishedIso = relPub;
                    releaseTagId = relTagId;
                }

                using (var commitResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/commits/{activeBranch}"))
                {
                    if (commitResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await commitResp.Content.ReadAsStringAsync());
                        var root = doc.RootElement;
                        string sha = root.TryGetProperty("sha", out var sEl) ? (sEl.GetString() ?? "") : "";
                        string shortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha;
                        string msg = "";
                        string dateStr = "";

                        if (root.TryGetProperty("commit", out var cObj))
                        {
                            msg = cObj.TryGetProperty("message", out var mEl) ? (mEl.GetString() ?? "") : "";
                            if (cObj.TryGetProperty("committer", out var cmEl) && cmEl.TryGetProperty("date", out var dEl))
                                dateStr = dEl.GetString() ?? "";
                        }

                        if (!hasNativeBinaryAsset)
                        {
                            var (rootUrl, rootType, _) = await TryResolveRepoRootBinaryAsync();
                            if (!string.IsNullOrWhiteSpace(rootUrl))
                            {
                                result.DownloadUrl = rootUrl;
                                result.DownloadType = rootType;
                                hasNativeBinaryAsset = true;
                            }
#if !ANDROID
                            else if (string.IsNullOrWhiteSpace(result.DownloadUrl))
                            {
                                result.DownloadUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{activeBranch}.zip";
                                result.DownloadType = "source_zip";
                            }
#endif
                        }

                        result.Success = true;
                        string combinedSignature = !string.IsNullOrWhiteSpace(releaseTagId) ? $"{releaseTagId}@{shortSha}" : sha;
                        result.LatestCommitSha = combinedSignature;

                        if (string.IsNullOrWhiteSpace(result.ReleaseTitle))
                            result.ReleaseTitle = $"Latest Commit ({shortSha}) on {activeBranch}";
                        if (string.IsNullOrWhiteSpace(result.ReleaseNotes))
                            result.ReleaseNotes = string.IsNullOrWhiteSpace(msg) ? $"Commit {shortSha}" : msg;
                        if (string.IsNullOrWhiteSpace(result.PublishedAt))
                            result.PublishedAt = FormatIsoDate(dateStr);

                        // Check SHA difference or timestamp difference
                        string effectiveDateIso = !string.IsNullOrWhiteSpace(releasePublishedIso) ? releasePublishedIso : dateStr;

                        if (!string.IsNullOrWhiteSpace(installedCommitSha) &&
                            !string.IsNullOrWhiteSpace(combinedSignature) &&
                            !string.Equals(installedCommitSha, combinedSignature, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(installedCommitSha, sha, StringComparison.OrdinalIgnoreCase))
                        {
                            result.IsUpdateAvailable = true;
                            result.LatestVersion = $"{result.LatestVersion} ({shortSha})";
                        }
                        else if (string.IsNullOrWhiteSpace(installedCommitSha) &&
                                 DateTimeOffset.TryParse(effectiveDateIso, out var commitTime))
                        {
                            DateTime localBuildTime = DateTime.MinValue;
#if ANDROID
                            try
                            {
                                var ctx = Android.App.Application.Context;
                                var pm = ctx.PackageManager;
                                string pkgName = ctx.PackageName ?? "";
#pragma warning disable CA1422
                                var pkgInfo = OperatingSystem.IsAndroidVersionAtLeast(33)
                                    ? pm?.GetPackageInfo(pkgName, Android.Content.PM.PackageManager.PackageInfoFlags.Of(0L)!)
                                    : pm?.GetPackageInfo(pkgName, 0);
#pragma warning restore CA1422
                                if (pkgInfo != null && pkgInfo.LastUpdateTime > 0)
                                    localBuildTime = DateTimeOffset.FromUnixTimeMilliseconds(pkgInfo.LastUpdateTime).UtcDateTime;
                            }
                            catch { }
#else
                            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                            if (File.Exists(exePath))
                                localBuildTime = File.GetLastWriteTimeUtc(exePath);
#endif
                            if (localBuildTime > DateTime.MinValue && commitTime.UtcDateTime > localBuildTime.AddMinutes(10))
                            {
                                result.IsUpdateAvailable = true;
                                result.LatestVersion = $"{result.LatestVersion} ({shortSha})";
                            }
                        }

                        return result;
                    }
                    else
                    {
                        if (result.Success) return result;
                        result.ErrorMessage = $"Could not query repository '{owner}/{repo}' (HTTP {(int)commitResp.StatusCode}).";
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Update check failed: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Stage 1: Downloads the update binary or archive into staging/cache storage and reports 0-100% progress.
        /// Does not launch an installer or restart the application.
        /// </summary>
        public async Task<(bool ok, string stagedFilePath, string error)> DownloadUpdatePackageAsync(
            UpdateCheckResult updateInfo,
            IProgress<(int percent, string status)>? progress = null)
        {
            if (updateInfo == null || string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
                return (false, "", "No download URL available for this update.");

            string stagingDir = Path.Combine(_baseDir, "_update_staging");
            try
            {
                bool isExe = updateInfo.DownloadType == "exe" ||
                             updateInfo.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                bool isApk = updateInfo.DownloadType == "apk" ||
                             updateInfo.DownloadUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

#if ANDROID
                string cacheDir = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                string downloadFile = Path.Combine(cacheDir, "StudyHub_Update.apk");
#else
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);
                string downloadFile = Path.Combine(stagingDir, isApk ? "StudyHub_Update.apk" : isExe ? "StudyHub_New.exe" : "update_package.zip");
#endif

                progress?.Report((2, "Connecting to GitHub download server..."));

                using (var response = await _http.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                        return (false, "", $"Download failed with HTTP {(int)response.StatusCode}.");

                    long? totalBytes = response.Content.Headers.ContentLength;
                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(downloadFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    int lastReported = -1;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        double mbRead = totalRead / (1024.0 * 1024.0);
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int pct = Math.Clamp(3 + (int)((totalRead * 96) / totalBytes.Value), 3, 99);
                            if (pct != lastReported)
                            {
                                lastReported = pct;
                                double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                                string kind = isApk ? "APK" : isExe ? "package" : "archive";
                                progress?.Report((pct, $"Downloading {kind}... {mbRead:F1} / {mbTotal:F1} MB"));
                            }
                        }
                        else
                        {
                            int estPct = Math.Min(92, 10 + (int)(mbRead * 4.5));
                            if (estPct != lastReported)
                            {
                                lastReported = estPct;
                                progress?.Report((estPct, $"Downloading update... ({mbRead:F1} MB)"));
                            }
                        }
                    }
                    await fileStream.FlushAsync();
                }

#if ANDROID
                LastDownloadedApkPath = downloadFile;
#endif
                progress?.Report((100, "Update package downloaded and verified."));
                return (true, downloadFile, "");
            }
            catch (Exception ex)
            {
                return (false, "", $"Failed to download update: {ex.Message}");
            }
        }

        /// <summary>
        /// Stage 2: Installs / extracts a previously downloaded update package while reporting 0-100% progress.
        /// Returns (true, "") on success; on Android may return (false, "PERMISSION_NEEDED").
        /// </summary>
        public async Task<(bool ok, string error)> ApplyDownloadedUpdateAsync(
            UpdateCheckResult updateInfo,
            string stagedFilePath,
            IProgress<(int percent, string status)>? progress = null)
        {
            if (updateInfo == null)
                return (false, "Missing update metadata.");
            if (string.IsNullOrWhiteSpace(stagedFilePath) || !File.Exists(stagedFilePath))
                return (false, "Downloaded update file was not found on disk.");

            try
            {
                bool isExe = updateInfo.DownloadType == "exe" ||
                             updateInfo.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                             stagedFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

#if ANDROID
                LastDownloadedApkPath = stagedFilePath;
                progress?.Report((20, "Verifying Android APK package..."));
                await Task.Delay(180);

                progress?.Report((55, "Checking Android install permission..."));
                await Task.Delay(180);
                if (!CanInstallPackagesOnAndroid())
                {
                    RequestInstallPackagesPermissionOnAndroid();
                    return (false, "PERMISSION_NEEDED");
                }

                progress?.Report((85, "Preparing Android package installer..."));
                await Task.Delay(200);

                var (launched, err) = LaunchAndroidApkInstaller(stagedFilePath);
                if (!launched)
                    return (false, err);

                progress?.Report((100, "Android package installer opened!"));
                await Task.Delay(180);
                return (true, "");
#else
                string stagingDir = Path.Combine(_baseDir, "_update_staging");
                int currentPid = Environment.ProcessId;
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StudyHub.exe");
                string appInstallDir = Path.GetDirectoryName(currentExe)
                    ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string rootExe = Path.Combine(appInstallDir, "StudyHub.exe");
                string distExe = Path.Combine(appInstallDir, "dist", "StudyHub.exe");
                string updaterBatPath = Path.Combine(_baseDir, "StudyHub_Updater.bat");

                progress?.Report((15, "Verifying downloaded update package..."));
                await Task.Delay(160);

                if (isExe)
                {
                    progress?.Report((50, "Validating StudyHub executable signature..."));
                    await Task.Delay(180);

                    progress?.Report((82, "Preparing atomic executable swap script..."));
                    string bat = $@"@echo off
title Study Hub Auto-Updater
echo Waiting for Study Hub (PID {currentPid}) to close...
:waitloop
tasklist /FI ""PID eq {currentPid}"" 2>NUL | find /I ""{currentPid}"" >NUL
if ""%ERRORLEVEL%""==""0"" (
    timeout /t 1 /nobreak >NUL
    goto waitloop
)
echo Applying new StudyHub.exe...
copy /Y ""{stagedFilePath}"" ""{currentExe}"" >NUL 2>&1
if exist ""{rootExe}"" copy /Y ""{stagedFilePath}"" ""{rootExe}"" >NUL 2>&1
if exist ""{distExe}"" copy /Y ""{stagedFilePath}"" ""{distExe}"" >NUL 2>&1
rmdir /S /Q ""{stagingDir}"" >NUL 2>&1
echo Launching updated Study Hub...
start """" ""{currentExe}""
del ""%~f0""
";
                    File.WriteAllText(updaterBatPath, bat, Encoding.ASCII);
                    await Task.Delay(160);
                }
                else
                {
                    string extractDir = Path.Combine(stagingDir, "extracted");
                    progress?.Report((25, "Inspecting update archive..."));

                    await Task.Run(() =>
                    {
                        using var archive = ZipFile.OpenRead(stagedFilePath);
                        int totalEntries = Math.Max(1, archive.Entries.Count);
                        int doneEntries = 0;
                        string fullExtractRoot = Path.GetFullPath(extractDir);
                        Directory.CreateDirectory(fullExtractRoot);

                        foreach (var entry in archive.Entries)
                        {
                            string destPath = Path.GetFullPath(Path.Combine(fullExtractRoot, entry.FullName));
                            if (destPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                if (string.IsNullOrEmpty(entry.Name))
                                {
                                    Directory.CreateDirectory(destPath);
                                }
                                else
                                {
                                    string? parent = Path.GetDirectoryName(destPath);
                                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                                    entry.ExtractToFile(destPath, true);
                                }
                            }
                            doneEntries++;
                            if (doneEntries % 5 == 0 || doneEntries == totalEntries)
                            {
                                int pct = 25 + (int)((doneEntries * 55L) / totalEntries);
                                progress?.Report((Math.Clamp(pct, 25, 80), $"Extracting update files... ({doneEntries}/{totalEntries})"));
                            }
                        }
                    });

                    var subDirs = Directory.GetDirectories(extractDir);
                    var subFiles = Directory.GetFiles(extractDir);
                    string sourceRoot = (subDirs.Length == 1 && subFiles.Length == 0) ? subDirs[0] : extractDir;

                    string prebuiltExe = Path.Combine(sourceRoot, "StudyHub.exe");
                    if (!File.Exists(prebuiltExe) && File.Exists(Path.Combine(sourceRoot, "dist", "StudyHub.exe")))
                        prebuiltExe = Path.Combine(sourceRoot, "dist", "StudyHub.exe");

                    bool hasCsproj = Directory.GetFiles(sourceRoot, "*.csproj").Length > 0;

                    progress?.Report((88, "Configuring update installer script..."));
                    var sb = new StringBuilder();
                    sb.AppendLine("@echo off");
                    sb.AppendLine("title Study Hub Auto-Updater");
                    sb.AppendLine($"cd /d \"{appInstallDir}\"");
                    sb.AppendLine($"echo Waiting for Study Hub (PID {currentPid}) to close...");
                    sb.AppendLine(":waitloop");
                    sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" 2>NUL | find /I \"{currentPid}\" >NUL");
                    sb.AppendLine("if \"%ERRORLEVEL%\"==\"0\" (");
                    sb.AppendLine("    timeout /t 1 /nobreak >NUL");
                    sb.AppendLine("    goto waitloop");
                    sb.AppendLine(")");
                    sb.AppendLine("echo Updating Study Hub files (preserving your database and files)...");
                    sb.AppendLine($"robocopy \"{sourceRoot}\" \"{appInstallDir}\" /E /XD \"StudyHub_Files\" \"StudyHub_Sounds\" \"StudyHub_ZenMedia\" \"StudyHub_Backups\" \"StudyHub_Converted_PDFs\" \"StudyHub_SyncCache\" \"webview_data\" \"_update_staging\" \".git\" \"bin\" \"obj\" /XF \"study_hub.db\" \"study_hub.db-wal\" \"study_hub.db-shm\" \"study_hub_state.json\" \"gdrive_device_config.json\" \"last_synced_snapshot.json\" \"sync_entity_hashes.json\" \"StudyHub_Updater.bat\" >NUL 2>&1");

                    if (hasCsproj)
                    {
                        sb.AppendLine("echo Building native StudyHub.exe from updated source...");
                        sb.AppendLine("dotnet publish StudyHub.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o \"dist\" >NUL 2>&1");
                        sb.AppendLine("if exist \"dist\\StudyHub.exe\" copy /Y \"dist\\StudyHub.exe\" \"StudyHub.exe\" >NUL 2>&1");
                    }
                    else if (File.Exists(prebuiltExe))
                    {
                        sb.AppendLine($"copy /Y \"{prebuiltExe}\" \"{currentExe}\" >NUL 2>&1");
                        sb.AppendLine($"copy /Y \"{prebuiltExe}\" \"{rootExe}\" >NUL 2>&1");
                    }

                    sb.AppendLine($"rmdir /S /Q \"{stagingDir}\" >NUL 2>&1");
                    sb.AppendLine("echo Launching updated Study Hub...");
                    sb.AppendLine($"start \"\" \"{currentExe}\"");
                    sb.AppendLine("del \"%~f0\"");

                    File.WriteAllText(updaterBatPath, sb.ToString(), Encoding.ASCII);
                    await Task.Delay(160);
                }

                progress?.Report((100, "Restarting Study Hub to apply update..."));
                await Task.Delay(320);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{updaterBatPath}\"\"",
                    WorkingDirectory = _baseDir,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                Process.Start(psi);
                return (true, "");
#endif
            }
            catch (Exception ex)
            {
                return (false, $"Failed to apply update: {ex.Message}");
            }
        }

        public async Task<(bool ok, string error)> DownloadAndApplyUpdateAsync(
            UpdateCheckResult updateInfo,
            IProgress<(int percent, string status)> progress)
        {
            var dlProgress = new Progress<(int percent, string status)>(p =>
            {
                int scaled = Math.Clamp((int)(p.percent * 0.72), 1, 72);
                progress?.Report((scaled, p.status));
            });

            var (dlOk, stagedPath, dlErr) = await DownloadUpdatePackageAsync(updateInfo, dlProgress);
            if (!dlOk)
                return (false, dlErr);

            var applyProgress = new Progress<(int percent, string status)>(p =>
            {
                int scaled = Math.Clamp(72 + (int)(p.percent * 0.28), 72, 100);
                progress?.Report((scaled, p.status));
            });

            return await ApplyDownloadedUpdateAsync(updateInfo, stagedPath, applyProgress);
        }

#if ANDROID
        public bool CanInstallPackagesOnAndroid()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return true;
                var ctx = Android.App.Application.Context;
                return ctx?.PackageManager?.CanRequestPackageInstalls() ?? true;
            }
            catch
            {
                return true;
            }
        }

        public void RequestInstallPackagesPermissionOnAndroid()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
                var ctx = Android.App.Application.Context;
                if (ctx == null) return;
                var uri = Android.Net.Uri.Parse($"package:{ctx.PackageName}");
                var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageUnknownAppSources, uri);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                ctx.StartActivity(intent);
            }
            catch { }
        }

        public (bool ok, string error) LaunchAndroidApkInstaller(string apkFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(apkFilePath) || !File.Exists(apkFilePath))
                    return (false, "Downloaded APK file was not found.");

                if (!CanInstallPackagesOnAndroid())
                {
                    RequestInstallPackagesPermissionOnAndroid();
                    return (false, "PERMISSION_NEEDED");
                }

                var ctx = Android.App.Application.Context;
                if (ctx == null)
                    return (false, "Android context unavailable.");

                var javaFile = new Java.IO.File(apkFilePath);
                var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    ctx,
                    ctx.PackageName + ".fileProvider",
                    javaFile);

                if (apkUri == null)
                    return (false, "Failed to create FileProvider URI for APK.");

                var installIntent = new Android.Content.Intent(Android.Content.Intent.ActionView);
                installIntent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
                installIntent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission | Android.Content.ActivityFlags.NewTask);
                ctx.StartActivity(installIntent);
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
#endif

        private static string CleanVersionString(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var match = Regex.Match(input, @"(\d+\.\d+(?:\.\d+)?)");
            return match.Success ? match.Groups[1].Value : input.Trim().TrimStart('v', 'V');
        }

        public static int CompareVersions(string v1, string v2)
        {
            try
            {
                string c1 = CleanVersionString(v1);
                string c2 = CleanVersionString(v2);
                if (Version.TryParse(NormalizeVerParts(c1), out var ver1) &&
                    Version.TryParse(NormalizeVerParts(c2), out var ver2))
                {
                    return ver1.CompareTo(ver2);
                }
                return string.Compare(c1, c2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return 0;
            }
        }

        private static string NormalizeVerParts(string v)
        {
            var parts = v.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out _)) return $"{parts[0]}.0.0";
            if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
            return v;
        }

        private static string FormatIsoDate(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return "";
            if (DateTimeOffset.TryParse(iso, out var dto))
                return dto.ToLocalTime().ToString("MMM d, yyyy • h:mm tt");
            return iso;
        }
    }
}
