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
        public string CurrentVersion { get; set; } = "3.0.0";
        public string LatestVersion { get; set; } = "3.0.0";
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
        public const string CurrentAppVersion = "3.0.0";
        public const int CurrentAppBuild = 300;

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

                // 1. Strategy A: Check GitHub Releases (/releases/latest or /releases?per_page=1)
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
                        releasePublishedIso = pub;
                        releaseTagId = !string.IsNullOrWhiteSpace(tag) ? $"rel:{tag}:{relId}" : $"rel:{relId}";

                        string cleanRemoteVer = CleanVersionString(tag);
                        if (string.IsNullOrWhiteSpace(cleanRemoteVer)) cleanRemoteVer = CleanVersionString(name);

                        string assetUrl = "";
                        string assetType = "";
                        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assetsEl.EnumerateArray())
                            {
                                string aName = asset.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                                string aUrl = asset.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "") : "";
#if ANDROID
                                if (aName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                                {
                                    assetUrl = aUrl;
                                    assetType = "apk";
                                    hasNativeBinaryAsset = true;
                                    break;
                                }
#else
                                if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    assetUrl = aUrl;
                                    assetType = "exe";
                                    hasNativeBinaryAsset = true;
                                    break;
                                }
                                if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(assetUrl))
                                {
                                    assetUrl = aUrl;
                                    assetType = "zip";
                                    hasNativeBinaryAsset = true;
                                }
#endif
                            }
                        }

                        if (string.IsNullOrEmpty(assetUrl))
                        {
                            // Fallback to repo root binary (StudyHub.apk / StudyHub.exe) ignoring GitHub's auto-generated Source code zips
                            var (rootUrl, rootType, _) = await TryResolveRepoRootBinaryAsync();
                            if (!string.IsNullOrWhiteSpace(rootUrl))
                            {
                                assetUrl = rootUrl;
                                assetType = rootType;
                                hasNativeBinaryAsset = true;
                            }
#if !ANDROID
                            else
                            {
                                assetUrl = !string.IsNullOrEmpty(zipball)
                                    ? zipball
                                    : $"https://github.com/{owner}/{repo}/archive/refs/heads/{activeBranch}.zip";
                                assetType = "source_zip";
                            }
#endif
                        }

                        if (!string.IsNullOrWhiteSpace(tag) || !string.IsNullOrWhiteSpace(name))
                        {
                            result.Success = true;
                            result.LatestVersion = !string.IsNullOrWhiteSpace(cleanRemoteVer) ? cleanRemoteVer : CurrentAppVersion;
                            result.ReleaseTitle = string.IsNullOrWhiteSpace(name) ? $"Release {tag}" : name;
                            result.ReleaseNotes = string.IsNullOrWhiteSpace(body) ? "Release found on GitHub." : body;
                            result.PublishedAt = FormatIsoDate(pub);
                            result.DownloadUrl = assetUrl;
                            result.DownloadType = assetType;
                            result.LatestCommitSha = releaseTagId;

                            if (!string.IsNullOrWhiteSpace(cleanRemoteVer) && CompareVersions(cleanRemoteVer, CurrentAppVersion) > 0)
                            {
                                result.IsUpdateAvailable = true;
                                return result;
                            }
                        }
                    }
                }
                catch { }

                // 2. Strategy B: Check raw version.json in active branch
                try
                {
                    string rawVerUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{activeBranch}/version.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    using var verResp = await _http.GetAsync(rawVerUrl);
                    if (verResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await verResp.Content.ReadAsStringAsync());
                        var root = doc.RootElement;
                        string remoteVer = root.TryGetProperty("version", out var vEl) ? (vEl.GetString() ?? "") : "";
                        int remoteBuild = root.TryGetProperty("build", out var bEl) && bEl.TryGetInt32(out int bVal) ? bVal : 0;
                        string notes = root.TryGetProperty("notes", out var nEl) ? (nEl.GetString() ?? "") : "";
                        string customDl = root.TryGetProperty("download_url", out var dEl) ? (dEl.GetString() ?? "") : "";
#if ANDROID
                        if (root.TryGetProperty("apk_url", out var apkEl) && !string.IsNullOrWhiteSpace(apkEl.GetString()))
                            customDl = apkEl.GetString() ?? customDl;
                        else if (root.TryGetProperty("android_download_url", out var aApkEl) && !string.IsNullOrWhiteSpace(aApkEl.GetString()))
                            customDl = aApkEl.GetString() ?? customDl;
#endif

                        string cleanVer = CleanVersionString(remoteVer);
                        if (!string.IsNullOrWhiteSpace(cleanVer))
                        {
                            result.Success = true;
                            result.LatestVersion = cleanVer;
                            if (string.IsNullOrWhiteSpace(result.ReleaseTitle))
                                result.ReleaseTitle = $"Study Hub v{cleanVer}";
                            if (string.IsNullOrWhiteSpace(result.ReleaseNotes) || result.ReleaseNotes == "Release found on GitHub.")
                                result.ReleaseNotes = string.IsNullOrWhiteSpace(notes) ? $"Version {cleanVer} from {owner}/{repo} ({activeBranch})" : notes;

                            // Preserve already-found release native binary if present; otherwise apply customDl / repo root binary
                            if (!hasNativeBinaryAsset)
                            {
                                if (!string.IsNullOrWhiteSpace(customDl))
                                {
                                    bool isWantedBinary =
#if ANDROID
                                        customDl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
#else
                                        customDl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || customDl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
#endif
                                    if (isWantedBinary)
                                    {
                                        result.DownloadUrl = customDl;
                                        result.DownloadType = customDl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? "apk"
                                            : customDl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "zip";
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
                                        result.DownloadUrl = !string.IsNullOrWhiteSpace(customDl)
                                            ? customDl
                                            : $"https://github.com/{owner}/{repo}/archive/refs/heads/{activeBranch}.zip";
                                        result.DownloadType = "source_zip";
                                    }
#endif
                                }
                            }

                            if (CompareVersions(cleanVer, CurrentAppVersion) > 0 || remoteBuild > CurrentAppBuild)
                            {
                                result.IsUpdateAvailable = true;
                                return result;
                            }
                        }
                    }
                }
                catch { }

                // 3. Strategy C: Check latest commit on active branch (and ensure binary URL is present)
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

        public async Task<(bool ok, string error)> DownloadAndApplyUpdateAsync(
            UpdateCheckResult updateInfo,
            IProgress<(int percent, string status)> progress)
        {
            if (updateInfo == null || string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
                return (false, "No download URL available for this update.");

            string stagingDir = Path.Combine(_baseDir, "_update_staging");
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);

                progress?.Report((5, "Connecting to GitHub download server..."));

                bool isExe = updateInfo.DownloadType == "exe" ||
                             updateInfo.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                bool isApk = updateInfo.DownloadType == "apk" ||
                             updateInfo.DownloadUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

#if ANDROID
                string cacheDir = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                string downloadFile = Path.Combine(cacheDir, "StudyHub_Update.apk");
#else
                string downloadFile = Path.Combine(stagingDir, isApk ? "StudyHub_Update.apk" : isExe ? "StudyHub_New.exe" : "update_package.zip");
#endif

                using (var response = await _http.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                        return (false, $"Download failed with HTTP {(int)response.StatusCode}.");

                    long? totalBytes = response.Content.Headers.ContentLength;
                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(downloadFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        double mbRead = totalRead / (1024.0 * 1024.0);
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
#if ANDROID
                            int pct = 5 + (int)((totalRead * 90) / totalBytes.Value);
                            double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                            progress?.Report((Math.Clamp(pct, 5, 95), $"Downloading APK... {mbRead:F1} / {mbTotal:F1} MB"));
#else
                            int pct = 10 + (int)((totalRead * 65) / totalBytes.Value);
                            progress?.Report((Math.Clamp(pct, 10, 75), $"Downloading update... ({mbRead:F1} MB)"));
#endif
                        }
                        else
                        {
                            progress?.Report((50, $"Downloading update... ({mbRead:F1} MB)"));
                        }
                    }
                    await fileStream.FlushAsync();
                }

#if ANDROID
                LastDownloadedApkPath = downloadFile;
                progress?.Report((100, "Download complete! Launching Android installer..."));
                return (true, "");
#else
                int currentPid = Environment.ProcessId;
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(_baseDir, "StudyHub.exe");
                string rootExe = Path.Combine(_baseDir, "StudyHub.exe");
                string distExe = Path.Combine(_baseDir, "dist", "StudyHub.exe");

                string updaterBatPath = Path.Combine(_baseDir, "StudyHub_Updater.bat");

                if (isExe)
                {
                    progress?.Report((90, "Preparing atomic executable swap..."));
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
copy /Y ""{downloadFile}"" ""{currentExe}"" >NUL 2>&1
if exist ""{rootExe}"" copy /Y ""{downloadFile}"" ""{rootExe}"" >NUL 2>&1
if exist ""{distExe}"" copy /Y ""{downloadFile}"" ""{distExe}"" >NUL 2>&1
rmdir /S /Q ""{stagingDir}"" >NUL 2>&1
echo Launching updated Study Hub...
start """" ""{currentExe}""
del ""%~f0""
";
                    File.WriteAllText(updaterBatPath, bat, Encoding.ASCII);
                }
                else
                {
                    progress?.Report((80, "Extracting update archive..."));
                    string extractDir = Path.Combine(stagingDir, "extracted");
                    ZipFile.ExtractToDirectory(downloadFile, extractDir, true);

                    // If zip has a single root folder (like owner-repo-sha), step into it
                    var subDirs = Directory.GetDirectories(extractDir);
                    var subFiles = Directory.GetFiles(extractDir);
                    string sourceRoot = (subDirs.Length == 1 && subFiles.Length == 0) ? subDirs[0] : extractDir;

                    // Check if extracted folder has a prebuilt StudyHub.exe or if it's source code
                    string prebuiltExe = Path.Combine(sourceRoot, "StudyHub.exe");
                    if (!File.Exists(prebuiltExe) && File.Exists(Path.Combine(sourceRoot, "dist", "StudyHub.exe")))
                        prebuiltExe = Path.Combine(sourceRoot, "dist", "StudyHub.exe");

                    bool hasCsproj = Directory.GetFiles(sourceRoot, "*.csproj").Length > 0;

                    progress?.Report((92, "Configuring update installer script..."));

                    var sb = new StringBuilder();
                    sb.AppendLine("@echo off");
                    sb.AppendLine("title Study Hub Auto-Updater");
                    sb.AppendLine($"cd /d \"{_baseDir}\"");
                    sb.AppendLine($"echo Waiting for Study Hub (PID {currentPid}) to close...");
                    sb.AppendLine(":waitloop");
                    sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" 2>NUL | find /I \"{currentPid}\" >NUL");
                    sb.AppendLine("if \"%ERRORLEVEL%\"==\"0\" (");
                    sb.AppendLine("    timeout /t 1 /nobreak >NUL");
                    sb.AppendLine("    goto waitloop");
                    sb.AppendLine(")");
                    sb.AppendLine("echo Updating Study Hub files (preserving your database and files)...");

                    // Copy updated source/app files while preserving user data folders & db
                    sb.AppendLine($"robocopy \"{sourceRoot}\" \"{_baseDir}\" /E /XD \"StudyHub_Files\" \"StudyHub_Sounds\" \"StudyHub_Backups\" \"StudyHub_Converted_PDFs\" \"webview_data\" \"_update_staging\" \".git\" \"bin\" \"obj\" /XF \"study_hub.db\" \"study_hub_state.json\" \"StudyHub_Updater.bat\" >NUL 2>&1");

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
                }

                progress?.Report((100, "Restarting to apply update..."));
                await Task.Delay(350);

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
