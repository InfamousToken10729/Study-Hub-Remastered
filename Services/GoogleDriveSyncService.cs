using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StudyHub.Models;

namespace StudyHub.Services
{
    public sealed class CloudSyncExecutionResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public bool NeedsUserResolution { get; set; }
        public string Message { get; set; } = "";
        public bool LocalStateUpdated { get; set; }
        public SyncMergeOutcome? Outcome { get; set; }
        public AppStateData? OriginalRemoteState { get; set; }
    }

    public sealed class GoogleDriveSyncService
    {
        private const string DriveScope = "https://www.googleapis.com/auth/drive.file openid email profile";
        private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
        private const string DriveApiBase = "https://www.googleapis.com/drive/v3";
        private const string DriveUploadBase = "https://www.googleapis.com/upload/drive/v3";

        private const string RootFolderName = "StudyHub_Sync";
        private const string FilesFolderName = "Files";
        private const string SoundsFolderName = "Sounds";
        private const string SnapshotsFolderName = "Snapshots";
        private const string RemoteStateFileName = "study_hub_sync_state.json";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        private static readonly JsonSerializerOptions PrettyJsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly StorageService _storage;
        private readonly HttpClient _apiHttp;
        private readonly HttpClient _fileHttp;
        private readonly SemaphoreSlim _syncMutex = new(1, 1);
        private GoogleDriveConfig _config;
        private DateTime _lastSnapshotUploadUtc = DateTime.MinValue;

        // Pending OAuth PKCE session so manual code/URL paste works seamlessly too
        private string _pendingState = "";
        private string _pendingCodeVerifier = "";
        private string _pendingRedirectUri = "";

        public GoogleDriveConfig Config => _config;
        public SyncProgressReport CurrentStatus { get; private set; } = new();

        public event Action<SyncProgressReport>? StatusChanged;
        public event Action<AppStateData, SyncMergeOutcome>? SyncStateApplied;

        public GoogleDriveSyncService(StorageService storage)
        {
            _storage = storage;
            _apiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _fileHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

            _config = LoadConfig();
            _storage.SetSyncDeviceId(_config.DeviceId);
            UpdateInitialStatus();
        }

        private void UpdateInitialStatus()
        {
            if (!_config.HasClientCredentials)
            {
                PublishStatus(SyncStatusLevel.NotConfigured, "Cloud Setup Needed", "Import Google OAuth credentials to enable free Google Drive sync.");
            }
            else if (!_config.HasValidSession)
            {
                PublishStatus(SyncStatusLevel.SignedOut, "Not Signed In", "Client credentials saved. Sign in with Google to start syncing.");
            }
            else
            {
                string detail = string.IsNullOrWhiteSpace(_config.LastSyncedAtUtc)
                    ? $"Signed in as {_config.UserEmail}"
                    : $"Last synced {FormatRelativeTime(_config.LastSyncedAtUtc)} ({_config.UserEmail})";
                PublishStatus(SyncStatusLevel.Idle, "Google Drive Connected", detail, _config.LastSyncedAtUtc);
            }
        }

        private void PublishStatus(SyncStatusLevel level, string headline, string detail = "", string syncedAtUtc = "", int conflicts = 0)
        {
            CurrentStatus = new SyncProgressReport
            {
                Level = level,
                Headline = headline,
                Detail = detail,
                SyncedAtUtc = syncedAtUtc,
                PendingConflictCount = conflicts
            };
            try { StatusChanged?.Invoke(CurrentStatus); } catch { }
        }

        public GoogleDriveConfig LoadConfig()
        {
            try
            {
                if (File.Exists(_storage.DriveConfigPath))
                {
                    string raw = File.ReadAllText(_storage.DriveConfigPath, Encoding.UTF8);
                    var cfg = JsonSerializer.Deserialize<GoogleDriveConfig>(raw, JsonOpts);
                    if (cfg != null)
                    {
                        cfg.Normalize();
                        return cfg;
                    }
                }
            }
            catch { }

            var fresh = new GoogleDriveConfig();
            fresh.Normalize();
            return fresh;
        }

        public void SaveConfig()
        {
            try
            {
                _config.Normalize();
                _storage.SetSyncDeviceId(_config.DeviceId);
                string json = JsonSerializer.Serialize(_config, PrettyJsonOpts);
                string tmp = _storage.DriveConfigPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, _storage.DriveConfigPath, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveDriveConfig error: {ex.Message}");
            }
        }

        public (bool Ok, string Message) ApplyClientCredentials(string clientId, string clientSecret)
        {
            string cid = (clientId ?? "").Trim();
            string csec = (clientSecret ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cid) || !cid.Contains(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Invalid Google Client ID. Expected '<id>.apps.googleusercontent.com'.");
            }

            bool credsChanged = !string.Equals(_config.ClientId, cid, StringComparison.Ordinal) ||
                                !string.Equals(_config.ClientSecret, csec, StringComparison.Ordinal);

            _config.ClientId = cid;
            _config.ClientSecret = csec;
            if (credsChanged)
            {
                _config.AccessToken = "";
                _config.RefreshToken = "";
                _config.TokenExpiresAtUtc = "";
                _config.IsSignedIn = false;
                _config.UserEmail = "";
                _config.UserName = "";
            }
            SaveConfig();
            UpdateInitialStatus();
            return (true, "Google OAuth Client credentials saved.");
        }

        public (bool Ok, string Message) ImportCredentialsFromJsonOrCode(string rawText)
        {
            string trimmed = (rawText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return (false, "No credentials JSON or Quick-Pair code provided.");

            if (trimmed.StartsWith("SHPAIR1:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string b64 = trimmed["SHPAIR1:".Length..].Trim();
                    trimmed = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
                catch
                {
                    return (false, "Invalid StudyHub Quick-Pair code.");
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                string cid = "";
                string csec = "";

                if (root.TryGetProperty("clientId", out var cidProp))
                {
                    cid = cidProp.GetString() ?? "";
                    csec = root.TryGetProperty("clientSecret", out var csecProp) ? (csecProp.GetString() ?? "") : "";
                }
                else if (root.TryGetProperty("installed", out var installed))
                {
                    cid = installed.TryGetProperty("client_id", out var p1) ? (p1.GetString() ?? "") : "";
                    csec = installed.TryGetProperty("client_secret", out var p2) ? (p2.GetString() ?? "") : "";
                }
                else if (root.TryGetProperty("web", out var web))
                {
                    cid = web.TryGetProperty("client_id", out var p1) ? (p1.GetString() ?? "") : "";
                    csec = web.TryGetProperty("client_secret", out var p2) ? (p2.GetString() ?? "") : "";
                }

                return ApplyClientCredentials(cid, csec);
            }
            catch (Exception ex)
            {
                return (false, $"Could not parse Google credentials JSON: {ex.Message}");
            }
        }

        public string ExportQuickPairCode()
        {
            if (!_config.HasClientCredentials) return "";
            var payload = new GoogleQuickPairPayload
            {
                StudyHubPairVersion = 1,
                ClientId = _config.ClientId,
                ClientSecret = _config.ClientSecret,
                CreatedAtUtc = DateTime.UtcNow.ToString("O")
            };
            string json = JsonSerializer.Serialize(payload, JsonOpts);
            return "SHPAIR1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public string ExportQuickPairJson()
        {
            if (!_config.HasClientCredentials) return "";
            var payload = new GoogleQuickPairPayload
            {
                StudyHubPairVersion = 1,
                ClientId = _config.ClientId,
                ClientSecret = _config.ClientSecret,
                CreatedAtUtc = DateTime.UtcNow.ToString("O")
            };
            return JsonSerializer.Serialize(payload, PrettyJsonOpts);
        }

        public async Task<(bool Ok, string Message)> StartInteractiveSignInAsync(Func<string, Task> openBrowserCallback, CancellationToken ct = default)
        {
            if (!_config.HasClientCredentials)
                return (false, "Please configure Google OAuth Client credentials first.");

            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirectUri = $"http://127.0.0.1:{port}/callback";

                _pendingCodeVerifier = GeneratePkceVerifier();
                string codeChallenge = GeneratePkceChallenge(_pendingCodeVerifier);
                _pendingState = Guid.NewGuid().ToString("N");
                _pendingRedirectUri = redirectUri;

                string authUrl = BuildAuthorizeUrl(redirectUri, codeChallenge, _pendingState);

                PublishStatus(SyncStatusLevel.Syncing, "Waiting for Google Sign-In...", "Complete sign-in in your browser.");

                await openBrowserCallback(authUrl);

                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                acceptCts.CancelAfter(TimeSpan.FromMinutes(4));

                while (!acceptCts.IsCancellationRequested)
                {
                    var acceptTask = listener.AcceptTcpClientAsync(acceptCts.Token).AsTask();
                    using var client = await acceptTask;
                    using var stream = client.GetStream();

                    string requestLine = await ReadHttpRequestLineAsync(stream, acceptCts.Token);
                    if (string.IsNullOrWhiteSpace(requestLine)) continue;

                    string rawPath = ExtractPathFromHttpRequestLine(requestLine);
                    if (rawPath.StartsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteSimpleHttpResponseAsync(stream, "404 Not Found", "text/plain", "Not Found");
                        continue;
                    }

                    var query = ParseQueryString(rawPath);
                    if (query.TryGetValue("error", out var oauthErr))
                    {
                        await WriteOAuthHtmlResponseAsync(stream, false, $"Google sign-in denied: {oauthErr}");
                        return (false, $"Google sign-in failed: {oauthErr}");
                    }

                    if (!query.TryGetValue("code", out var authCode) || string.IsNullOrWhiteSpace(authCode))
                    {
                        await WriteOAuthHtmlResponseAsync(stream, false, "Missing authorization code from Google callback.");
                        continue;
                    }

                    if (query.TryGetValue("state", out var returnedState) &&
                        !string.Equals(returnedState, _pendingState, StringComparison.Ordinal))
                    {
                        await WriteOAuthHtmlResponseAsync(stream, false, "OAuth state verification failed.");
                        return (false, "OAuth state mismatch. Please retry sign-in.");
                    }

                    await WriteOAuthHtmlResponseAsync(stream, true, "Study Hub is now authorized! You can return to the app.");
                    listener.Stop();

                    return await ExchangeAuthCodeAsync(authCode, _pendingRedirectUri, _pendingCodeVerifier, ct);
                }

                return (false, "Sign-in timed out. You can try again or paste the redirect URL.");
            }
            catch (OperationCanceledException)
            {
                return (false, "Sign-in cancelled or timed out.");
            }
            catch (Exception ex)
            {
                return (false, $"Sign-in failed: {ex.Message}");
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }
        }

        public async Task<(bool Ok, string Message)> CompleteSignInFromManualInputAsync(string codeOrRedirectUrl, CancellationToken ct = default)
        {
            string input = (codeOrRedirectUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(input))
                return (false, "Please paste the authorization code or callback URL.");
            if (string.IsNullOrWhiteSpace(_pendingCodeVerifier) || string.IsNullOrWhiteSpace(_pendingRedirectUri))
                return (false, "No active sign-in session. Click Sign In with Google first.");

            string code = input;
            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var query = ParseQueryString(input);
                if (!query.TryGetValue("code", out code) || string.IsNullOrWhiteSpace(code))
                    return (false, "No authorization 'code' parameter found in that URL.");
            }

            return await ExchangeAuthCodeAsync(code, _pendingRedirectUri, _pendingCodeVerifier, ct);
        }

        private async Task<(bool Ok, string Message)> ExchangeAuthCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = _config.ClientId,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier
            };
            if (!string.IsNullOrWhiteSpace(_config.ClientSecret))
                form["client_secret"] = _config.ClientSecret;

            using var content = new FormUrlEncodedContent(form);
            using var resp = await _apiHttp.PostAsync(TokenEndpoint, content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                UpdateInitialStatus();
                return (false, $"Token exchange failed ({(int)resp.StatusCode}): {ExtractOAuthError(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            _config.AccessToken = root.GetProperty("access_token").GetString() ?? "";
            if (root.TryGetProperty("refresh_token", out var refProp))
            {
                string rt = refProp.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(rt)) _config.RefreshToken = rt;
            }
            int expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
            _config.TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn)).ToString("O");
            _config.IsSignedIn = true;

            await FetchUserProfileAsync(ct);
            SaveConfig();
            PublishStatus(SyncStatusLevel.Idle, "Google Drive Connected", $"Signed in as {_config.UserEmail}", _config.LastSyncedAtUtc);
            return (true, $"Signed in as {_config.UserEmail}");
        }

        private async Task FetchUserProfileAsync(CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                using var resp = await _apiHttp.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return;
                string body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("email", out var em)) _config.UserEmail = em.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("name", out var nm)) _config.UserName = nm.GetString() ?? "";
            }
            catch { }
        }

        public void SignOut()
        {
            _config.AccessToken = "";
            _config.RefreshToken = "";
            _config.TokenExpiresAtUtc = "";
            _config.IsSignedIn = false;
            _config.UserEmail = "";
            _config.UserName = "";
            _config.RemoteStateFileId = "";
            _config.LastSyncedRemoteMd5 = "";
            _config.LastSyncedRemoteVersion = 0;
            SaveConfig();
            UpdateInitialStatus();
        }

        public async Task<bool> EnsureValidAccessTokenAsync(CancellationToken ct = default)
        {
            if (!_config.HasClientCredentials || !_config.HasValidSession) return false;

            bool needsRefresh = string.IsNullOrWhiteSpace(_config.AccessToken);
            if (!needsRefresh && DateTimeOffset.TryParse(_config.TokenExpiresAtUtc, out var exp))
            {
                needsRefresh = exp <= DateTimeOffset.UtcNow.AddMinutes(3);
            }
            else if (string.IsNullOrWhiteSpace(_config.TokenExpiresAtUtc))
            {
                needsRefresh = true;
            }

            if (!needsRefresh) return true;

            var form = new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["refresh_token"] = _config.RefreshToken,
                ["grant_type"] = "refresh_token"
            };
            if (!string.IsNullOrWhiteSpace(_config.ClientSecret))
                form["client_secret"] = _config.ClientSecret;

            using var content = new FormUrlEncodedContent(form);
            using var resp = await _apiHttp.PostAsync(TokenEndpoint, content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 401)
                {
                    if (body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                    {
                        _config.IsSignedIn = false;
                        _config.AccessToken = "";
                        SaveConfig();
                        PublishStatus(SyncStatusLevel.SignedOut, "Google Session Expired", "Please sign in with Google again.");
                    }
                }
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            _config.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
            _config.TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn)).ToString("O");
            SaveConfig();
            return true;
        }

        public async Task<CloudSyncExecutionResult> SyncNowAsync(
            AppStateData currentLocalState,
            SyncResolutionMode mode = SyncResolutionMode.SmartMerge,
            IReadOnlyList<SyncConflictItem>? resolvedConflicts = null,
            bool mismatchAcknowledged = false,
            bool allowBinaryFileSyncOverride = false,
            CancellationToken ct = default)
        {
            if (!_config.HasClientCredentials)
            {
                PublishStatus(SyncStatusLevel.NotConfigured, "Cloud Setup Needed", "Import Google OAuth credentials first.");
                return new CloudSyncExecutionResult { Success = false, Message = "Google OAuth credentials are not configured." };
            }
            if (!_config.HasValidSession)
            {
                PublishStatus(SyncStatusLevel.SignedOut, "Not Signed In", "Please sign in with Google Drive.");
                return new CloudSyncExecutionResult { Success = false, Message = "Please sign in with Google first." };
            }
            if (!IsNetworkOnline())
            {
                PublishStatus(SyncStatusLevel.Offline, "Offline — Changes Saved Locally", "Will sync automatically when back online.");
                return new CloudSyncExecutionResult { Success = false, Message = "No internet connection. Your local changes are safe and queued." };
            }

            await _syncMutex.WaitAsync(ct);
            try
            {
                PublishStatus(SyncStatusLevel.Syncing, "Syncing with Google Drive...", "Checking cloud state & verifying integrity...");

                if (!await EnsureValidAccessTokenAsync(ct))
                {
                    return new CloudSyncExecutionResult { Success = false, Message = "Google authentication required or temporarily unreachable." };
                }

                await EnsureDriveFolderHierarchyAsync(ct);

                // Retry up to 2 times if another device races to update study_hub_sync_state.json mid-merge
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var (remoteMeta, remoteState) = await PullRemoteStateAsync(ct);
                    var baseSnapshot = _storage.LoadBaseSnapshot();

                    var localFiles = _storage.GetLocalBinaryFilesInventory(currentLocalState);
                    var localSounds = _storage.GetLocalBinarySoundsInventory(currentLocalState);

                    bool shouldSyncFiles = (currentLocalState.Settings.SyncFilesEnabled || allowBinaryFileSyncOverride) &&
                                           (allowBinaryFileSyncOverride || CanTransferBinaryFiles(currentLocalState.Settings));

                    Dictionary<string, RemoteBinaryFileMeta> remoteFiles = new(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, RemoteBinaryFileMeta> remoteSounds = new(StringComparer.OrdinalIgnoreCase);

                    if (shouldSyncFiles && !string.IsNullOrWhiteSpace(_config.DriveFilesFolderId))
                    {
                        remoteFiles = await ListFolderFilesAsync(_config.DriveFilesFolderId, ct);
                    }
                    if (shouldSyncFiles && !string.IsNullOrWhiteSpace(_config.DriveSoundsFolderId))
                    {
                        remoteSounds = await ListFolderFilesAsync(_config.DriveSoundsFolderId, ct);
                    }

                    var outcome = SmartSyncMergeEngine.EvaluateAndMerge(
                        currentLocalState,
                        remoteState,
                        baseSnapshot,
                        mode,
                        resolvedConflicts,
                        _config.DeviceId,
                        localFiles,
                        remoteFiles,
                        localSounds,
                        remoteSounds,
                        mismatchAcknowledged);

                    if (outcome.RequiresUserAction)
                    {
                        string msg = outcome.MismatchReport.RequiresConfirmation
                            ? outcome.MismatchReport.ReasonTitle
                            : $"{outcome.Conflicts.Count} sync conflict(s) require your choice.";
                        PublishStatus(
                            outcome.Conflicts.Count > 0 ? SyncStatusLevel.Conflict : SyncStatusLevel.NeedsAttention,
                            msg,
                            outcome.StatusSummary,
                            _config.LastSyncedAtUtc,
                            outcome.Conflicts.Count);

                        return new CloudSyncExecutionResult
                        {
                            Success = false,
                            NeedsUserResolution = true,
                            Message = msg,
                            Outcome = outcome,
                            OriginalRemoteState = remoteState
                        };
                    }

                    // Before applying ForcePull or significant cloud changes, create local pre-sync recovery snapshot
                    if (remoteState != null && (mode == SyncResolutionMode.ForcePullCloud || outcome.CloudChangesAppliedCount > 0 || mismatchAcknowledged))
                    {
                        await _storage.CreatePreSyncLocalBackupAsync(currentLocalState, "pre_sync");
                    }

                    // Keep cloud Time Machine rolling snapshot before modifying remote state
                    bool isSignificantOverwrite = mode == SyncResolutionMode.ForcePushLocal || mismatchAcknowledged;
                    if (remoteState != null && (isSignificantOverwrite || (DateTime.UtcNow - _lastSnapshotUploadUtc).TotalMinutes >= 30))
                    {
                        string snapSummary = $"{remoteState.Subjects.Count} courses, {SmartSyncMergeEngine.ComputeCounts(remoteState).TotalItems} items";
                        await UploadCloudSnapshotInternalAsync(remoteState, snapSummary, ct);
                        _lastSnapshotUploadUtc = DateTime.UtcNow;
                    }

                    // Execute binary file downloads/uploads if allowed
                    int binaryTransferred = 0;
                    if (shouldSyncFiles)
                    {
                        foreach (var dl in outcome.FilesToDownload)
                        {
                            if (remoteFiles.TryGetValue(dl.FileName, out var rf))
                            {
                                PublishStatus(SyncStatusLevel.Syncing, "Downloading Study Files...", dl.DisplayTitle);
                                string dest = Path.Combine(_storage.FilesDir, Path.GetFileName(dl.FileName));
                                if (await DownloadDriveFileAtomicAsync(rf.DriveFileId, dest, ct))
                                {
                                    binaryTransferred++;
                                    UpdateMergedFileHash(outcome.MergedState, dl.FileName, dest);
                                }
                            }
                        }
                        foreach (var dl in outcome.SoundsToDownload)
                        {
                            if (remoteSounds.TryGetValue(dl.FileName, out var rs))
                            {
                                PublishStatus(SyncStatusLevel.Syncing, "Downloading Custom Sounds...", dl.DisplayTitle);
                                string dest = Path.Combine(_storage.SoundsDir, Path.GetFileName(dl.FileName));
                                if (await DownloadDriveFileAtomicAsync(rs.DriveFileId, dest, ct))
                                {
                                    binaryTransferred++;
                                    UpdateMergedSoundHash(outcome.MergedState, dl.FileName, dest);
                                }
                            }
                        }
                        foreach (var ul in outcome.FilesToUpload)
                        {
                            string src = _storage.GetStudyFilePath(ul.FileName);
                            if (File.Exists(src))
                            {
                                PublishStatus(SyncStatusLevel.Syncing, "Uploading Study Files...", ul.DisplayTitle);
                                string? existingDriveId = remoteFiles.TryGetValue(ul.FileName, out var rf) ? rf.DriveFileId : null;
                                await UploadBinaryFileAsync(_config.DriveFilesFolderId, Path.GetFileName(ul.FileName), src, existingDriveId, ct);
                                binaryTransferred++;
                            }
                        }
                        foreach (var ul in outcome.SoundsToUpload)
                        {
                            string src = _storage.GetSoundFilePath(ul.FileName);
                            if (File.Exists(src))
                            {
                                PublishStatus(SyncStatusLevel.Syncing, "Uploading Custom Sounds...", ul.DisplayTitle);
                                string? existingDriveId = remoteSounds.TryGetValue(ul.FileName, out var rs) ? rs.DriveFileId : null;
                                await UploadBinaryFileAsync(_config.DriveSoundsFolderId, Path.GetFileName(ul.FileName), src, existingDriveId, ct);
                                binaryTransferred++;
                            }
                        }
                    }

                    // Optimistic concurrency check: verify nobody else updated remoteState between pull and push
                    if (remoteMeta != null && !string.IsNullOrWhiteSpace(remoteMeta.DriveFileId))
                    {
                        var latestMeta = await GetDriveFileMetaAsync(remoteMeta.DriveFileId, ct);
                        if (latestMeta != null &&
                            remoteMeta.SizeBytes > 0 &&
                            !string.IsNullOrWhiteSpace(latestMeta.Md5Checksum) &&
                            !string.IsNullOrWhiteSpace(remoteMeta.Md5Checksum) &&
                            !string.Equals(latestMeta.Md5Checksum, remoteMeta.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                        {
                            // Concurrent remote push happened while we merged/uploaded binaries; re-merge safely on attempt 2!
                            if (attempt == 0)
                            {
                                mismatchAcknowledged = false;
                                continue;
                            }
                        }
                    }

                    string nowUtc = DateTime.UtcNow.ToString("O");
                    string finalDetail = binaryTransferred > 0
                        ? $"{outcome.StatusSummary} ({binaryTransferred} file(s) transferred)"
                        : outcome.StatusSummary;

                    outcome.MergedState.Settings.LastSyncStatusText = $"Synced {DateTime.Now:t}";
                    await _storage.SaveMergedStateAsync(outcome.MergedState);

                    string uploadedMd5 = await PushRemoteStateAsync(outcome.MergedState, remoteMeta?.DriveFileId, ct);
                    _storage.SaveBaseSnapshot(outcome.MergedState);

                    _config.LastSyncedRemoteMd5 = uploadedMd5;
                    _config.LastSyncedAtUtc = nowUtc;
                    _config.LastSyncResultMessage = finalDetail;
                    SaveConfig();

                    PublishStatus(SyncStatusLevel.Synced, "Synced with Google Drive", finalDetail, nowUtc);
                    try { SyncStateApplied?.Invoke(outcome.MergedState, outcome); } catch { }

                    return new CloudSyncExecutionResult
                    {
                        Success = true,
                        LocalStateUpdated = true,
                        Message = finalDetail,
                        Outcome = outcome
                    };
                }

                return new CloudSyncExecutionResult { Success = false, Message = "Concurrent sync conflict persisted after retry; please tap Sync Now again." };
            }
            catch (Exception ex)
            {
                PublishStatus(SyncStatusLevel.Error, "Sync Failed", ex.Message, _config.LastSyncedAtUtc);
                return new CloudSyncExecutionResult { Success = false, Message = $"Sync error: {ex.Message}" };
            }
            finally
            {
                _syncMutex.Release();
            }
        }

        public async Task<List<CloudSnapshotInfo>> ListCloudSnapshotsAsync(CancellationToken ct = default)
        {
            if (!await EnsureValidAccessTokenAsync(ct)) return new List<CloudSnapshotInfo>();
            await EnsureDriveFolderHierarchyAsync(ct);

            string q = Uri.EscapeDataString($"'{_config.DriveSnapshotsFolderId}' in parents and trashed=false");
            string fields = Uri.EscapeDataString("files(id,name,size,createdTime,description)");
            string url = $"{DriveApiBase}/files?q={q}&orderBy=createdTime desc&pageSize=30&fields={fields}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            using var resp = await _apiHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new List<CloudSnapshotInfo>();

            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var list = new List<CloudSnapshotInfo>();
            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var f in files.EnumerateArray())
                {
                    long sz = 0;
                    if (f.TryGetProperty("size", out var szEl))
                    {
                        if (szEl.ValueKind == JsonValueKind.String) long.TryParse(szEl.GetString(), out sz);
                        else if (szEl.ValueKind == JsonValueKind.Number) sz = szEl.GetInt64();
                    }
                    list.Add(new CloudSnapshotInfo
                    {
                        DriveFileId = f.GetProperty("id").GetString() ?? "",
                        FileName = f.GetProperty("name").GetString() ?? "",
                        CreatedAtUtc = f.TryGetProperty("createdTime", out var ctEl) ? (ctEl.GetString() ?? "") : "",
                        SizeBytes = sz,
                        SummaryText = f.TryGetProperty("description", out var descEl) ? (descEl.GetString() ?? "") : ""
                    });
                }
            }
            return list;
        }

        public async Task<AppStateData?> DownloadCloudSnapshotAsync(string driveFileId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(driveFileId) || !await EnsureValidAccessTokenAsync(ct)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files/{driveFileId}?alt=media");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            using var resp = await _apiHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct);
            var state = JsonSerializer.Deserialize<AppStateData>(json, JsonOpts);
            state?.Normalize();
            return state;
        }

        public async Task<(bool Ok, string Message)> UploadManualBackupSnapshotAsync(AppStateData state, CancellationToken ct = default)
        {
            try
            {
                if (!await EnsureValidAccessTokenAsync(ct)) return (false, "Not signed in to Google Drive.");
                await EnsureDriveFolderHierarchyAsync(ct);
                string summary = $"Manual snapshot: {state.Subjects.Count} courses, {SmartSyncMergeEngine.ComputeCounts(state).TotalItems} items";
                await UploadCloudSnapshotInternalAsync(state, summary, ct);
                return (true, "Saved Time Machine snapshot to Google Drive.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to create snapshot: {ex.Message}");
            }
        }

        private async Task UploadCloudSnapshotInternalAsync(AppStateData state, string summary, CancellationToken ct)
        {
            try
            {
                string safeDevice = Regex.Replace(_config.DeviceName ?? "Device", @"[^A-Za-z0-9_\-]", "_");
                if (safeDevice.Length > 24) safeDevice = safeDevice[..24];
                string fileName = $"snapshot_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_{safeDevice}.json";
                string json = JsonSerializer.Serialize(state, JsonOpts);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                await MultipartUploadBytesAsync(_config.DriveSnapshotsFolderId, fileName, "application/json", bytes, null, $"{_config.DeviceName} — {summary}", ct);

                // Keep last 15 snapshots in Drive
                var existing = await ListCloudSnapshotsAsync(ct);
                if (existing.Count > 15)
                {
                    foreach (var old in existing.Skip(15))
                    {
                        try
                        {
                            using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{DriveApiBase}/files/{old.DriveFileId}");
                            delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                            await _apiHttp.SendAsync(delReq, ct);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private async Task EnsureDriveFolderHierarchyAsync(CancellationToken ct)
        {
            _config.DriveRootFolderId = await EnsureFolderAsync(null, RootFolderName, _config.DriveRootFolderId, ct);
            _config.DriveFilesFolderId = await EnsureFolderAsync(_config.DriveRootFolderId, FilesFolderName, _config.DriveFilesFolderId, ct);
            _config.DriveSoundsFolderId = await EnsureFolderAsync(_config.DriveRootFolderId, SoundsFolderName, _config.DriveSoundsFolderId, ct);
            _config.DriveSnapshotsFolderId = await EnsureFolderAsync(_config.DriveRootFolderId, SnapshotsFolderName, _config.DriveSnapshotsFolderId, ct);
            SaveConfig();
        }

        private async Task<string> EnsureFolderAsync(string? parentId, string folderName, string? cachedId, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(cachedId))
            {
                using var checkReq = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files/{cachedId}?fields=id,trashed");
                checkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                using var checkResp = await _apiHttp.SendAsync(checkReq, ct);
                if (checkResp.IsSuccessStatusCode)
                {
                    string checkBody = await checkResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(checkBody);
                    bool trashed = doc.RootElement.TryGetProperty("trashed", out var tr) && tr.GetBoolean();
                    if (!trashed) return cachedId;
                }
            }

            string parentClause = string.IsNullOrWhiteSpace(parentId) ? "'root' in parents" : $"'{parentId}' in parents";
            string q = Uri.EscapeDataString($"name='{folderName}' and mimeType='application/vnd.google-apps.folder' and {parentClause} and trashed=false");
            using var searchReq = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files?q={q}&fields=files(id)&pageSize=5");
            searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            using var searchResp = await _apiHttp.SendAsync(searchReq, ct);
            if (searchResp.IsSuccessStatusCode)
            {
                string sb = await searchResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(sb);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                {
                    return files[0].GetProperty("id").GetString() ?? "";
                }
            }

            var meta = new Dictionary<string, object>
            {
                ["name"] = folderName,
                ["mimeType"] = "application/vnd.google-apps.folder"
            };
            if (!string.IsNullOrWhiteSpace(parentId)) meta["parents"] = new[] { parentId };

            using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{DriveApiBase}/files?fields=id");
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            createReq.Content = new StringContent(JsonSerializer.Serialize(meta), Encoding.UTF8, "application/json");
            using var createResp = await _apiHttp.SendAsync(createReq, ct);
            string createBody = await createResp.Content.ReadAsStringAsync(ct);
            createResp.EnsureSuccessStatusCode();
            using var cdoc = JsonDocument.Parse(createBody);
            return cdoc.RootElement.GetProperty("id").GetString() ?? "";
        }

        private async Task<(RemoteBinaryFileMeta? Meta, AppStateData? State)> PullRemoteStateAsync(CancellationToken ct)
        {
            RemoteBinaryFileMeta? meta = null;
            if (!string.IsNullOrWhiteSpace(_config.RemoteStateFileId))
            {
                meta = await GetDriveFileMetaAsync(_config.RemoteStateFileId, ct);
            }

            if (meta == null)
            {
                string q = Uri.EscapeDataString($"name='{RemoteStateFileName}' and '{_config.DriveRootFolderId}' in parents and trashed=false");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files?q={q}&orderBy=modifiedTime desc&fields=files(id,name,size,md5Checksum)&pageSize=5");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                using var resp = await _apiHttp.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                    {
                        var f = files[0];
                        meta = ParseFileMetaElement(f);
                        _config.RemoteStateFileId = meta.DriveFileId;
                    }
                }
            }

            if (meta == null || string.IsNullOrWhiteSpace(meta.DriveFileId))
                return (null, null);

            using var dlReq = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files/{meta.DriveFileId}?alt=media");
            dlReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            using var dlResp = await _apiHttp.SendAsync(dlReq, ct);
            if (!dlResp.IsSuccessStatusCode) return (meta, null);

            string rawJson = await dlResp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<AppStateData>(rawJson, JsonOpts);
            parsed?.Normalize();
            return (meta, parsed);
        }

        private async Task<RemoteBinaryFileMeta?> GetDriveFileMetaAsync(string fileId, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files/{fileId}?fields=id,name,size,md5Checksum,trashed");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            using var resp = await _apiHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("trashed", out var tr) && tr.GetBoolean()) return null;
            return ParseFileMetaElement(doc.RootElement);
        }

        private async Task<string> PushRemoteStateAsync(AppStateData state, string? existingFileId, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(state, JsonOpts);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var (driveId, md5) = await MultipartUploadBytesAsync(_config.DriveRootFolderId, RemoteStateFileName, "application/json", bytes, existingFileId, $"Synced by {_config.DeviceName}", ct);
            _config.RemoteStateFileId = driveId;
            return md5;
        }

        private async Task<Dictionary<string, RemoteBinaryFileMeta>> ListFolderFilesAsync(string folderId, CancellationToken ct)
        {
            var result = new Dictionary<string, RemoteBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);
            string pageToken = "";
            do
            {
                string q = Uri.EscapeDataString($"'{folderId}' in parents and trashed=false");
                string url = $"{DriveApiBase}/files?q={q}&pageSize=1000&fields=nextPageToken,files(id,name,size,md5Checksum)";
                if (!string.IsNullOrWhiteSpace(pageToken)) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                using var resp = await _apiHttp.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) break;

                string body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("files", out var files))
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        var meta = ParseFileMetaElement(f);
                        if (!string.IsNullOrWhiteSpace(meta.FileName) && !result.ContainsKey(meta.FileName))
                            result[meta.FileName] = meta;
                    }
                }
                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt) ? (pt.GetString() ?? "") : "";
            }
            while (!string.IsNullOrWhiteSpace(pageToken));

            return result;
        }

        private static RemoteBinaryFileMeta ParseFileMetaElement(JsonElement f)
        {
            long sz = 0;
            if (f.TryGetProperty("size", out var szEl))
            {
                if (szEl.ValueKind == JsonValueKind.String) long.TryParse(szEl.GetString(), out sz);
                else if (szEl.ValueKind == JsonValueKind.Number) sz = szEl.GetInt64();
            }
            return new RemoteBinaryFileMeta
            {
                DriveFileId = f.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "",
                FileName = f.TryGetProperty("name", out var nmEl) ? (nmEl.GetString() ?? "") : "",
                SizeBytes = sz,
                Md5Checksum = f.TryGetProperty("md5Checksum", out var mEl) ? (mEl.GetString() ?? "") : ""
            };
        }

        private async Task<(string FileId, string Md5)> MultipartUploadBytesAsync(
            string folderId,
            string fileName,
            string contentType,
            byte[] payload,
            string? existingFileId,
            string? description,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(existingFileId))
            {
                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{DriveUploadBase}/files/{existingFileId}?uploadType=media&fields=id,md5Checksum");
                patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                patchReq.Content = new ByteArrayContent(payload);
                patchReq.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                using var patchResp = await _apiHttp.SendAsync(patchReq, ct);
                if (patchResp.IsSuccessStatusCode)
                {
                    string pb = await patchResp.Content.ReadAsStringAsync(ct);
                    using var pdoc = JsonDocument.Parse(pb);
                    return (
                        pdoc.RootElement.GetProperty("id").GetString() ?? existingFileId,
                        pdoc.RootElement.TryGetProperty("md5Checksum", out var m) ? (m.GetString() ?? "") : ""
                    );
                }
            }

            string boundary = "StudyHubDrive_" + Guid.NewGuid().ToString("N");
            var metaObj = new Dictionary<string, object>
            {
                ["name"] = fileName,
                ["parents"] = new[] { folderId }
            };
            if (!string.IsNullOrWhiteSpace(description)) metaObj["description"] = description;
            string metaJson = JsonSerializer.Serialize(metaObj);

            using var ms = new MemoryStream();
            void WriteAscii(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
            WriteAscii($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{metaJson}\r\n");
            WriteAscii($"--{boundary}\r\nContent-Type: {contentType}\r\n\r\n");
            ms.Write(payload, 0, payload.Length);
            WriteAscii($"\r\n--{boundary}--\r\n");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{DriveUploadBase}/files?uploadType=multipart&fields=id,md5Checksum");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            req.Content = new ByteArrayContent(ms.ToArray());
            req.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/related; boundary={boundary}");

            using var resp = await _apiHttp.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(body);
            return (
                doc.RootElement.GetProperty("id").GetString() ?? "",
                doc.RootElement.TryGetProperty("md5Checksum", out var md5El) ? (md5El.GetString() ?? "") : ""
            );
        }

        private async Task UploadBinaryFileAsync(string folderId, string fileName, string localPath, string? existingFileId, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(existingFileId))
            {
                using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{DriveUploadBase}/files/{existingFileId}?uploadType=media&fields=id");
                patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                patchReq.Content = new StreamContent(fs);
                patchReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var patchResp = await _fileHttp.SendAsync(patchReq, ct);
                if (patchResp.IsSuccessStatusCode) return;
            }

            string boundary = "StudyHubBin_" + Guid.NewGuid().ToString("N");
            string metaJson = JsonSerializer.Serialize(new { name = fileName, parents = new[] { folderId } });

            using var mp = new MultipartContent("related", boundary);
            var jsonPart = new StringContent(metaJson, Encoding.UTF8, "application/json");
            mp.Add(jsonPart);

            using var fs2 = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var streamPart = new StreamContent(fs2);
            streamPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            mp.Add(streamPart);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{DriveUploadBase}/files?uploadType=multipart&fields=id");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            req.Content = mp;

            using var resp = await _fileHttp.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }

        private async Task<bool> DownloadDriveFileAtomicAsync(string driveFileId, string destPath, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                Directory.CreateDirectory(_storage.SyncCacheDir);
                string tempPath = Path.Combine(_storage.SyncCacheDir, $"{Guid.NewGuid():N}_{Path.GetFileName(destPath)}.part");

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveApiBase}/files/{driveFileId}?alt=media");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
                using var resp = await _fileHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return false;

                using (var srcStream = await resp.Content.ReadAsStreamAsync(ct))
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await srcStream.CopyToAsync(fs, ct);
                }

                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                {
                    try { File.Delete(tempPath); } catch { }
                    return false;
                }

                File.Move(tempPath, destPath, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void UpdateMergedFileHash(AppStateData state, string fileName, string path)
        {
            try
            {
                long sz = new FileInfo(path).Length;
                string sha = StorageService.ComputeFileSha256(path);
                foreach (var p in state.Pdfs.Where(x => string.Equals(x.Filename, fileName, StringComparison.OrdinalIgnoreCase))) { p.FileSizeBytes = sz; p.Sha256 = sha; }
                foreach (var v in state.Videos.Where(x => string.Equals(x.Filename, fileName, StringComparison.OrdinalIgnoreCase))) { v.FileSizeBytes = sz; v.Sha256 = sha; }
                foreach (var r in state.Recordings.Where(x => string.Equals(x.Filename, fileName, StringComparison.OrdinalIgnoreCase))) { r.FileSizeBytes = sz; r.Sha256 = sha; }
            }
            catch { }
        }

        private static void UpdateMergedSoundHash(AppStateData state, string fileName, string path)
        {
            try
            {
                long sz = new FileInfo(path).Length;
                string sha = StorageService.ComputeFileSha256(path);
                foreach (var s in state.CustomSounds.Where(x => string.Equals(x.Filename, fileName, StringComparison.OrdinalIgnoreCase))) { s.FileSizeBytes = sz; s.Sha256 = sha; }
            }
            catch { }
        }

        private static bool IsNetworkOnline()
        {
            try
            {
#if ANDROID
                var access = Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess;
                return access == Microsoft.Maui.Networking.NetworkAccess.Internet ||
                       access == Microsoft.Maui.Networking.NetworkAccess.ConstrainedInternet;
#else
                return NetworkInterface.GetIsNetworkAvailable();
#endif
            }
            catch
            {
                return true;
            }
        }

        private static bool CanTransferBinaryFiles(UserSettings settings)
        {
            if (!settings.WifiOnlyFileSync) return true;
#if ANDROID
            try
            {
                var profiles = Microsoft.Maui.Networking.Connectivity.Current.ConnectionProfiles;
                if (profiles.Contains(Microsoft.Maui.Networking.ConnectionProfile.WiFi) ||
                    profiles.Contains(Microsoft.Maui.Networking.ConnectionProfile.Ethernet))
                    return true;
                return false;
            }
            catch { return true; }
#else
            return true;
#endif
        }

        private string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
        {
            return $"{AuthEndpoint}?client_id={Uri.EscapeDataString(_config.ClientId)}" +
                   $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                   $"&response_type=code" +
                   $"&scope={Uri.EscapeDataString(DriveScope)}" +
                   $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                   $"&code_challenge_method=S256" +
                   $"&access_type=offline" +
                   $"&prompt=consent" +
                   $"&state={Uri.EscapeDataString(state)}";
        }

        private static string GeneratePkceVerifier()
        {
            var bytes = RandomNumberGenerator.GetBytes(48);
            return Base64UrlEncode(bytes);
        }

        private static string GeneratePkceChallenge(string verifier)
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static async Task<string> ReadHttpRequestLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var buf = new byte[4096];
            int total = 0;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            while (total < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), readCts.Token);
                if (n <= 0) break;
                total += n;
                string partial = Encoding.ASCII.GetString(buf, 0, total);
                if (partial.Contains("\r\n\r\n", StringComparison.Ordinal) || partial.Contains("\n", StringComparison.Ordinal))
                {
                    int nl = partial.IndexOf('\n');
                    return (nl >= 0 ? partial[..nl] : partial).Trim();
                }
            }
            return "";
        }

        private static string ExtractPathFromHttpRequestLine(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : "/";
        }

        private static Dictionary<string, string> ParseQueryString(string urlOrPath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int qIdx = urlOrPath.IndexOf('?');
            if (qIdx < 0 || qIdx == urlOrPath.Length - 1) return dict;
            string qs = urlOrPath[(qIdx + 1)..];
            foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                string k = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
                string v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                dict[k] = v;
            }
            return dict;
        }

        private static async Task WriteOAuthHtmlResponseAsync(NetworkStream stream, bool success, string message)
        {
            string redirectTag = success && OperatingSystem.IsAndroid()
                ? "<meta http-equiv=\"refresh\" content=\"0;url=studyhub://oauth-callback?status=ok\" />"
                : "";
            string btnHtml = success && OperatingSystem.IsAndroid()
                ? "<p style=\"margin-top:20px;\"><a href=\"studyhub://oauth-callback?status=ok\" style=\"background:#8B5CF6;color:white;padding:12px 24px;border-radius:10px;text-decoration:none;font-weight:bold;\">Return to Study Hub</a></p>"
                : "";
            string title = success ? "Study Hub Connected" : "Authorization Failed";
            string color = success ? "#10B981" : "#EF4444";
            string html = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>{redirectTag}<title>{title}</title></head>" +
                          $"<body style=\"background:#090910;color:#F4F4F8;font-family:sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;padding:24px;text-align:center;\">" +
                          $"<div style=\"max-width:440px;background:#131320;border:1px solid #2A2A3D;border-radius:16px;padding:32px;\">" +
                          $"<h2 style=\"color:{color};margin-top:0;\">{title}</h2>" +
                          $"<p style=\"color:#C4C4D8;line-height:1.5;\">{WebUtility.HtmlEncode(message)}</p>{btnHtml}</div></body></html>";
            await WriteSimpleHttpResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html);
        }

        private static async Task WriteSimpleHttpResponseAsync(NetworkStream stream, string status, string contentType, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes);
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        }

        private static string ExtractOAuthError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error_description", out var ed)) return ed.GetString() ?? body;
                if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? body;
            }
            catch { }
            return body.Length > 180 ? body[..180] : body;
        }

        public static string FormatRelativeTime(string utcIso)
        {
            if (string.IsNullOrWhiteSpace(utcIso)) return "never";
            if (!DateTimeOffset.TryParse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dto))
                return "recently";
            var span = DateTimeOffset.UtcNow - dto;
            if (span.TotalSeconds < 45) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            return dto.ToLocalTime().ToString("MMM dd, HH:mm");
        }
    }
}
