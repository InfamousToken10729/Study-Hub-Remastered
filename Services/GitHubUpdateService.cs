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

        public GitHubUpdateService(string baseDir)
        {
            _baseDir = baseDir;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
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

            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                result.ErrorMessage = "Please paste a GitHub repository or release link first.";
                return result;
            }

            try
            {
                if (!TryParseGitHubRepo(rawUrl, out string owner, out string repo, out string branch))
                {
                    // Check if it's a direct link to a .exe or .zip file
                    if (rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        (rawUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || rawUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Success = true;
                        result.IsUpdateAvailable = true;
                        result.LatestVersion = "Direct Package";
                        result.ReleaseTitle = Path.GetFileName(new Uri(rawUrl).LocalPath);
                        result.ReleaseNotes = "Direct update package URL configured.";
                        result.DownloadUrl = rawUrl.Trim();
                        result.DownloadType = rawUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "zip";
                        result.RepoDisplay = rawUrl.Trim();
                        return result;
                    }

                    result.ErrorMessage = "Invalid GitHub URL. Example: https://github.com/username/repository";
                    return result;
                }

                result.RepoDisplay = $"{owner}/{repo}";
                string defaultBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch;

                // 0. Query repo metadata to get actual default_branch if not specified
                try
                {
                    using var repoResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}");
                    if (repoResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await repoResp.Content.ReadAsStringAsync());
                        if (string.IsNullOrWhiteSpace(branch) && doc.RootElement.TryGetProperty("default_branch", out var dbEl))
                        {
                            defaultBranch = dbEl.GetString() ?? "main";
                        }
                    }
                    else if ((int)repoResp.StatusCode == 404)
                    {
                        result.ErrorMessage = $"GitHub repository '{owner}/{repo}' was not found (or is private).";
                        return result;
                    }
                }
                catch { }

                // 1. Strategy A: Check GitHub Releases (/releases/latest or /releases)
                try
                {
                    using var relResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
                    if (relResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await relResp.Content.ReadAsStringAsync());
                        var root = doc.RootElement;
                        string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "") : "";
                        string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? tag) : tag;
                        string body = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "") : "";
                        string pub = root.TryGetProperty("published_at", out var pEl) ? (pEl.GetString() ?? "") : "";
                        string zipball = root.TryGetProperty("zipball_url", out var zEl) ? (zEl.GetString() ?? "") : "";

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
                                if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    assetUrl = aUrl;
                                    assetType = "exe";
                                    break;
                                }
                                if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(assetUrl))
                                {
                                    assetUrl = aUrl;
                                    assetType = "zip";
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(assetUrl))
                        {
                            assetUrl = !string.IsNullOrEmpty(zipball)
                                ? zipball
                                : $"https://github.com/{owner}/{repo}/archive/refs/heads/{defaultBranch}.zip";
                            assetType = "source_zip";
                        }

                        if (!string.IsNullOrWhiteSpace(cleanRemoteVer))
                        {
                            result.Success = true;
                            result.LatestVersion = cleanRemoteVer;
                            result.ReleaseTitle = string.IsNullOrWhiteSpace(name) ? $"Release {tag}" : name;
                            result.ReleaseNotes = string.IsNullOrWhiteSpace(body) ? "New release available on GitHub." : body;
                            result.PublishedAt = FormatIsoDate(pub);
                            result.DownloadUrl = assetUrl;
                            result.DownloadType = assetType;
                            result.IsUpdateAvailable = CompareVersions(cleanRemoteVer, CurrentAppVersion) > 0;
                            if (result.IsUpdateAvailable)
                                return result;
                        }
                    }
                }
                catch { }

                // 2. Strategy B: Check raw version.json in repository default branch
                try
                {
                    string rawVerUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{defaultBranch}/version.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    using var verResp = await _http.GetAsync(rawVerUrl);
                    if (verResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await verResp.Content.ReadAsStringAsync());
                        var root = doc.RootElement;
                        string remoteVer = root.TryGetProperty("version", out var vEl) ? (vEl.GetString() ?? "") : "";
                        int remoteBuild = root.TryGetProperty("build", out var bEl) && bEl.TryGetInt32(out int bVal) ? bVal : 0;
                        string notes = root.TryGetProperty("notes", out var nEl) ? (nEl.GetString() ?? "") : "";
                        string customDl = root.TryGetProperty("download_url", out var dEl) ? (dEl.GetString() ?? "") : "";

                        string cleanVer = CleanVersionString(remoteVer);
                        if (!string.IsNullOrWhiteSpace(cleanVer))
                        {
                            result.Success = true;
                            result.LatestVersion = cleanVer;
                            result.ReleaseTitle = $"Study Hub v{cleanVer}";
                            result.ReleaseNotes = string.IsNullOrWhiteSpace(notes) ? $"Version {cleanVer} from {owner}/{repo} ({defaultBranch})" : notes;
                            result.DownloadUrl = !string.IsNullOrWhiteSpace(customDl)
                                ? customDl
                                : $"https://github.com/{owner}/{repo}/archive/refs/heads/{defaultBranch}.zip";
                            result.DownloadType = !string.IsNullOrWhiteSpace(customDl) && customDl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                ? "exe"
                                : "source_zip";

                            if (CompareVersions(cleanVer, CurrentAppVersion) > 0 || remoteBuild > CurrentAppBuild)
                            {
                                result.IsUpdateAvailable = true;
                                return result;
                            }
                        }
                    }
                }
                catch { }

                // 3. Strategy C: Check latest commit on default branch
                using (var commitResp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/commits/{defaultBranch}"))
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

                        result.Success = true;
                        result.LatestCommitSha = sha;
                        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
                        {
                            result.DownloadUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{defaultBranch}.zip";
                            result.DownloadType = "source_zip";
                        }
                        if (string.IsNullOrWhiteSpace(result.ReleaseTitle))
                            result.ReleaseTitle = $"Latest Commit ({shortSha}) on {defaultBranch}";
                        if (string.IsNullOrWhiteSpace(result.ReleaseNotes))
                            result.ReleaseNotes = string.IsNullOrWhiteSpace(msg) ? $"Commit {shortSha}" : msg;
                        result.PublishedAt = FormatIsoDate(dateStr);

                        // If user had a previously recorded commit SHA and remote differs, mark update available
                        if (!string.IsNullOrWhiteSpace(installedCommitSha) &&
                            !string.IsNullOrWhiteSpace(sha) &&
                            !string.Equals(installedCommitSha, sha, StringComparison.OrdinalIgnoreCase))
                        {
                            result.IsUpdateAvailable = true;
                            result.LatestVersion = $"{result.LatestVersion} ({shortSha})";
                        }
                        else if (string.IsNullOrWhiteSpace(installedCommitSha) &&
                                 DateTimeOffset.TryParse(dateStr, out var commitTime))
                        {
                            // Compare against local executable build time (with 10-minute grace window)
                            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                            if (File.Exists(exePath))
                            {
                                var localBuildTime = File.GetLastWriteTimeUtc(exePath);
                                if (commitTime.UtcDateTime > localBuildTime.AddMinutes(10))
                                {
                                    result.IsUpdateAvailable = true;
                                    result.LatestVersion = $"{result.LatestVersion} ({shortSha})";
                                }
                            }
                        }

                        return result;
                    }
                    else
                    {
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

                string downloadFile = Path.Combine(stagingDir, isExe ? "StudyHub_New.exe" : "update_package.zip");

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
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int pct = 10 + (int)((totalRead * 65) / totalBytes.Value);
                            progress?.Report((Math.Clamp(pct, 10, 75), $"Downloading update... ({totalRead / 1024} KB)"));
                        }
                        else
                        {
                            progress?.Report((45, $"Downloading update... ({totalRead / 1024} KB)"));
                        }
                    }
                }

#if ANDROID
                progress?.Report((100, $"Downloaded update package to {downloadFile}"));
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
