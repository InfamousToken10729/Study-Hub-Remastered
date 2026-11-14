using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
#if WINDOWS
using Microsoft.Win32;
#endif
using StudyHub.Models;

namespace StudyHub.Services
{
    public class StorageService
    {
        public string BaseDir { get; }
        public string SqliteDbPath { get; }
        public string JsonMirrorPath { get; }
        public string FilesDir { get; }
        public string SoundsDir { get; }
        public string BackupsDir { get; }
        public string DefaultExportDir { get; }
        public string WebViewDataDir { get; }
        public string SyncCacheDir { get; }
        public string DriveConfigPath { get; }
        public string BaseSnapshotPath { get; }
        public string HashManifestPath { get; }

        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private readonly object _trackLock = new();
        private SyncEntityHashManifest _hashManifest = new();
        private string _syncDeviceId = "";

        public event Action? StateSaved;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        private static readonly JsonSerializerOptions _prettyJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public StorageService()
        {
            BaseDir = ResolveWritableDataDirectory();
            SqliteDbPath = Path.Combine(BaseDir, "study_hub.db");
            JsonMirrorPath = Path.Combine(BaseDir, "study_hub_state.json");
            FilesDir = Path.Combine(BaseDir, "StudyHub_Files");
            SoundsDir = Path.Combine(BaseDir, "StudyHub_Sounds");
            BackupsDir = Path.Combine(BaseDir, "StudyHub_Backups");
            DefaultExportDir = Path.Combine(BaseDir, "StudyHub_Converted_PDFs");
            WebViewDataDir = Path.Combine(BaseDir, "webview_data");
            SyncCacheDir = Path.Combine(BaseDir, "StudyHub_SyncCache");
            DriveConfigPath = Path.Combine(BaseDir, "gdrive_device_config.json");
            BaseSnapshotPath = Path.Combine(BaseDir, "last_synced_snapshot.json");
            HashManifestPath = Path.Combine(BaseDir, "sync_entity_hashes.json");

            Directory.CreateDirectory(FilesDir);
            Directory.CreateDirectory(SoundsDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(DefaultExportDir);
            Directory.CreateDirectory(WebViewDataDir);
            Directory.CreateDirectory(SyncCacheDir);

            EnsureDatabaseTables();
        }

        public void SetSyncDeviceId(string deviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
                _syncDeviceId = deviceId.Trim();
        }

        private static string ResolveWritableDataDirectory()
        {
#if ANDROID
            string androidDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyHub");
            Directory.CreateDirectory(androidDir);
            return androidDir;
#else
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // If running from bin\Release\net9.0-windows... or dist\, check if parent workspace has study_hub.db
            var candidates = new List<string> { exeDir };
            DirectoryInfo? cursor = new DirectoryInfo(exeDir);
            for (int i = 0; i < 4 && cursor?.Parent != null; i++)
            {
                cursor = cursor.Parent;
                if (File.Exists(Path.Combine(cursor.FullName, "study_hub.db")) ||
                    Directory.Exists(Path.Combine(cursor.FullName, "StudyHub_Files")))
                {
                    candidates.Add(cursor.FullName);
                }
            }

            // Pick the directory that has the most populated study_hub.db or StudyHub_Files, or exeDir if writable
            string bestDir = exeDir;
            foreach (var dir in candidates)
            {
                string dbPath = Path.Combine(dir, "study_hub.db");
                if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
                {
                    // Check if exeDir already has study_hub.db; if exeDir is dist and parent has newer/populated data, sync
                    bestDir = dir;
                    if (string.Equals(Path.GetFileName(exeDir), "dist", StringComparison.OrdinalIgnoreCase))
                    {
                        bestDir = exeDir;
                    }
                    break;
                }
            }

            // Test writability
            try
            {
                string testFile = Path.Combine(bestDir, ".sh_write_test");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
                return bestDir;
            }
            catch
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GraduationStudyHub");
                Directory.CreateDirectory(appData);
                return appData;
            }
#endif
        }

        private void EnsureDatabaseTables()
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS app_state (
                        id INTEGER PRIMARY KEY,
                        data TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS study_files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        filename TEXT UNIQUE NOT NULL,
                        original_name TEXT NOT NULL,
                        folder TEXT DEFAULT 'Lectures',
                        subject_id TEXT NOT NULL
                    );
                ";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SQLite init warning: {ex.Message}");
            }
        }

        public AppStateData LoadState()
        {
            // 1. Try SQLite app_state table
            try
            {
                if (File.Exists(SqliteDbPath))
                {
                    using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT data FROM app_state WHERE id = 1;";
                    var raw = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrWhiteSpace(raw) && raw.Trim() != "{}")
                    {
                        var state = JsonSerializer.Deserialize<AppStateData>(raw, _jsonOptions);
                        if (state != null)
                        {
                            return FinalizeLoadedState(state);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SQLite load warning: {ex.Message}");
            }

            // 2. Fallback to JSON mirror if exists
            try
            {
                if (File.Exists(JsonMirrorPath))
                {
                    string raw = File.ReadAllText(JsonMirrorPath, Encoding.UTF8);
                    var state = JsonSerializer.Deserialize<AppStateData>(raw, _jsonOptions);
                    if (state != null)
                    {
                        return FinalizeLoadedState(state);
                    }
                }
            }
            catch { }

            // 3. Fallback to latest daily backup if exists
            try
            {
                var latestBackup = Directory.GetFiles(BackupsDir, "state_*.json")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
                if (latestBackup != null)
                {
                    string raw = File.ReadAllText(latestBackup, Encoding.UTF8);
                    var state = JsonSerializer.Deserialize<AppStateData>(raw, _jsonOptions);
                    if (state != null)
                    {
                        return FinalizeLoadedState(state);
                    }
                }
            }
            catch { }

            var fresh = new AppStateData();
            return FinalizeLoadedState(fresh);
        }

        private AppStateData FinalizeLoadedState(AppStateData state)
        {
            state.Normalize();
            BackfillMissingFileHashes(state);
            lock (_trackLock)
            {
                _hashManifest = LoadHashManifest();
                if (!_hashManifest.Initialized)
                {
                    SmartSyncMergeEngine.TrackLocalChanges(state, _hashManifest, _syncDeviceId, suppressTombstones: true);
                    SaveHashManifest(_hashManifest);
                }
            }
            return state;
        }

        private void BackfillMissingFileHashes(AppStateData state)
        {
            try
            {
                foreach (var pdf in state.Pdfs)
                {
                    if ((string.IsNullOrWhiteSpace(pdf.Sha256) || pdf.FileSizeBytes <= 0) && !string.IsNullOrWhiteSpace(pdf.Filename))
                    {
                        string path = GetStudyFilePath(pdf.Filename);
                        if (File.Exists(path))
                        {
                            pdf.FileSizeBytes = new FileInfo(path).Length;
                            pdf.Sha256 = ComputeFileSha256(path);
                        }
                    }
                }
                foreach (var vid in state.Videos)
                {
                    if ((string.IsNullOrWhiteSpace(vid.Sha256) || vid.FileSizeBytes <= 0) && !string.IsNullOrWhiteSpace(vid.Filename))
                    {
                        string path = GetStudyFilePath(vid.Filename);
                        if (File.Exists(path))
                        {
                            vid.FileSizeBytes = new FileInfo(path).Length;
                            vid.Sha256 = ComputeFileSha256(path);
                        }
                    }
                }
                foreach (var rec in state.Recordings)
                {
                    if ((string.IsNullOrWhiteSpace(rec.Sha256) || rec.FileSizeBytes <= 0) && !string.IsNullOrWhiteSpace(rec.Filename))
                    {
                        string path = GetStudyFilePath(rec.Filename);
                        if (File.Exists(path))
                        {
                            rec.FileSizeBytes = new FileInfo(path).Length;
                            rec.Sha256 = ComputeFileSha256(path);
                        }
                    }
                }
                foreach (var snd in state.CustomSounds)
                {
                    if ((string.IsNullOrWhiteSpace(snd.Sha256) || snd.FileSizeBytes <= 0) && !string.IsNullOrWhiteSpace(snd.Filename))
                    {
                        string path = GetSoundFilePath(snd.Filename);
                        if (File.Exists(path))
                        {
                            snd.FileSizeBytes = new FileInfo(path).Length;
                            snd.Sha256 = ComputeFileSha256(path);
                        }
                    }
                }
            }
            catch { }
        }

        public async Task SaveStateAsync(AppStateData state)
        {
            if (state == null) return;
            await _saveLock.WaitAsync();
            try
            {
                await Task.Run(() => SaveStateSyncInternal(state, suppressTombstones: false, raiseStateSaved: true));
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public async Task SaveMergedStateAsync(AppStateData mergedState)
        {
            if (mergedState == null) return;
            await _saveLock.WaitAsync();
            try
            {
                await Task.Run(() => SaveStateSyncInternal(mergedState, suppressTombstones: true, raiseStateSaved: false));
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public void SaveStateSync(AppStateData state)
        {
            SaveStateSyncInternal(state, suppressTombstones: false, raiseStateSaved: true);
        }

        private void SaveStateSyncInternal(AppStateData state, bool suppressTombstones, bool raiseStateSaved)
        {
            if (state == null) return;
            try
            {
                lock (_trackLock)
                {
                    if (!_hashManifest.Initialized)
                        _hashManifest = LoadHashManifest();
                    SmartSyncMergeEngine.TrackLocalChanges(state, _hashManifest, _syncDeviceId, suppressTombstones);
                    SaveHashManifest(_hashManifest);
                }

                string json = JsonSerializer.Serialize(state, _jsonOptions);
                string nowIso = DateTime.Now.ToString("O");

                // Save daily backup snapshot (once per day)
                SnapshotDailyBackup(json);

                // Write JSON mirror atomically
                string tmpMirror = JsonMirrorPath + ".tmp";
                File.WriteAllText(tmpMirror, json, Encoding.UTF8);
                File.Move(tmpMirror, JsonMirrorPath, true);

                // Write to SQLite study_hub.db
                using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO app_state (id, data, updated_at)
                    VALUES (1, $data, $updated)
                    ON CONFLICT(id) DO UPDATE SET data = $data, updated_at = $updated;
                ";
                cmd.Parameters.AddWithValue("$data", json);
                cmd.Parameters.AddWithValue("$updated", nowIso);
                cmd.ExecuteNonQuery();

                // Also keep dist\study_hub.db or parent study_hub.db in sync if both exist
                SyncAlternateDatabaseCopy(SqliteDbPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveState error: {ex.Message}");
            }

            if (raiseStateSaved)
            {
                try { StateSaved?.Invoke(); } catch { }
            }
        }

        public static string ComputeFileSha256(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return "";
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var hash = SHA256.HashData(fs);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return "";
            }
        }

        public AppStateData? LoadBaseSnapshot()
        {
            try
            {
                if (!File.Exists(BaseSnapshotPath)) return null;
                string raw = File.ReadAllText(BaseSnapshotPath, Encoding.UTF8);
                var snap = JsonSerializer.Deserialize<AppStateData>(raw, _jsonOptions);
                snap?.Normalize();
                return snap;
            }
            catch
            {
                return null;
            }
        }

        public void SaveBaseSnapshot(AppStateData state)
        {
            try
            {
                if (state == null) return;
                string json = JsonSerializer.Serialize(state, _jsonOptions);
                string tmp = BaseSnapshotPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, BaseSnapshotPath, true);
            }
            catch { }
        }

        public void ClearBaseSnapshot()
        {
            try
            {
                if (File.Exists(BaseSnapshotPath)) File.Delete(BaseSnapshotPath);
            }
            catch { }
        }

        private SyncEntityHashManifest LoadHashManifest()
        {
            try
            {
                if (File.Exists(HashManifestPath))
                {
                    string raw = File.ReadAllText(HashManifestPath, Encoding.UTF8);
                    var manifest = JsonSerializer.Deserialize<SyncEntityHashManifest>(raw, _jsonOptions);
                    if (manifest != null)
                    {
                        manifest.Normalize();
                        return manifest;
                    }
                }
            }
            catch { }
            return new SyncEntityHashManifest();
        }

        private void SaveHashManifest(SyncEntityHashManifest manifest)
        {
            try
            {
                string json = JsonSerializer.Serialize(manifest, _jsonOptions);
                string tmp = HashManifestPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, HashManifestPath, true);
            }
            catch { }
        }

        public async Task<string> CreatePreSyncLocalBackupAsync(AppStateData state, string tag)
        {
            if (state == null) return "";
            try
            {
                Directory.CreateDirectory(BackupsDir);
                string safeTag = Regex.Replace(tag ?? "sync", @"[^A-Za-z0-9_\-]", "_");
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string filePath = Path.Combine(BackupsDir, $"pre_{safeTag}_{stamp}.json");
                string json = JsonSerializer.Serialize(state, _jsonOptions);
                await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);

                var oldPreBackups = Directory.GetFiles(BackupsDir, "pre_*.json")
                    .OrderByDescending(f => f)
                    .Skip(25)
                    .ToList();
                foreach (var old in oldPreBackups)
                {
                    try { File.Delete(old); } catch { }
                }
                return filePath;
            }
            catch
            {
                return "";
            }
        }

        public Dictionary<string, LocalBinaryFileMeta> GetLocalBinaryFilesInventory(AppStateData state)
        {
            var dict = new Dictionary<string, LocalBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (state != null)
            {
                foreach (var p in state.Pdfs) if (!string.IsNullOrWhiteSpace(p.Filename)) filenames.Add(p.Filename);
                foreach (var v in state.Videos) if (!string.IsNullOrWhiteSpace(v.Filename)) filenames.Add(v.Filename);
                foreach (var r in state.Recordings) if (!string.IsNullOrWhiteSpace(r.Filename)) filenames.Add(r.Filename);
            }
            if (Directory.Exists(FilesDir))
            {
                foreach (var full in Directory.GetFiles(FilesDir))
                {
                    string name = Path.GetFileName(full);
                    if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) filenames.Add(name);
                }
            }

            foreach (var fn in filenames)
            {
                string fullPath = GetStudyFilePath(fn);
                if (File.Exists(fullPath))
                {
                    long len = new FileInfo(fullPath).Length;
                    string? stateSha = state?.Pdfs.FirstOrDefault(x => string.Equals(x.Filename, fn, StringComparison.OrdinalIgnoreCase))?.Sha256
                        ?? state?.Videos.FirstOrDefault(x => string.Equals(x.Filename, fn, StringComparison.OrdinalIgnoreCase))?.Sha256
                        ?? state?.Recordings.FirstOrDefault(x => string.Equals(x.Filename, fn, StringComparison.OrdinalIgnoreCase))?.Sha256;

                    dict[fn] = new LocalBinaryFileMeta
                    {
                        FileName = fn,
                        SizeBytes = len,
                        Sha256 = !string.IsNullOrWhiteSpace(stateSha) ? stateSha : ComputeFileSha256(fullPath)
                    };
                }
            }
            return dict;
        }

        public Dictionary<string, LocalBinaryFileMeta> GetLocalBinarySoundsInventory(AppStateData state)
        {
            var dict = new Dictionary<string, LocalBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (state != null)
            {
                foreach (var s in state.CustomSounds) if (!string.IsNullOrWhiteSpace(s.Filename)) filenames.Add(s.Filename);
            }
            if (Directory.Exists(SoundsDir))
            {
                foreach (var full in Directory.GetFiles(SoundsDir))
                {
                    string name = Path.GetFileName(full);
                    if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) filenames.Add(name);
                }
            }

            foreach (var fn in filenames)
            {
                string fullPath = GetSoundFilePath(fn);
                if (File.Exists(fullPath))
                {
                    long len = new FileInfo(fullPath).Length;
                    string? stateSha = state?.CustomSounds.FirstOrDefault(x => string.Equals(x.Filename, fn, StringComparison.OrdinalIgnoreCase))?.Sha256;
                    dict[fn] = new LocalBinaryFileMeta
                    {
                        FileName = fn,
                        SizeBytes = len,
                        Sha256 = !string.IsNullOrWhiteSpace(stateSha) ? stateSha : ComputeFileSha256(fullPath)
                    };
                }
            }
            return dict;
        }

        public string LoadRawStateJson()
        {
            // 1. Try SQLite app_state table
            try
            {
                if (File.Exists(SqliteDbPath))
                {
                    using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT data FROM app_state WHERE id = 1;";
                    var raw = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrWhiteSpace(raw) && raw.Trim() != "{}")
                    {
                        return raw;
                    }
                }
            }
            catch { }

            // 2. Fallback to JSON mirror
            try
            {
                if (File.Exists(JsonMirrorPath))
                {
                    string raw = File.ReadAllText(JsonMirrorPath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(raw) && raw.Trim() != "{}")
                        return raw;
                }
            }
            catch { }

            // 3. Fallback to latest daily backup
            try
            {
                var latestBackup = Directory.GetFiles(BackupsDir, "state_*.json")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
                if (latestBackup != null)
                {
                    string raw = File.ReadAllText(latestBackup, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(raw))
                        return raw;
                }
            }
            catch { }

            return "{}";
        }

        public async Task SaveRawStateJsonAsync(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;
            await _saveLock.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        string nowIso = DateTime.Now.ToString("O");
                        SnapshotDailyBackup(rawJson);

                        string tmpMirror = JsonMirrorPath + ".tmp";
                        File.WriteAllText(tmpMirror, rawJson, Encoding.UTF8);
                        File.Move(tmpMirror, JsonMirrorPath, true);

                        using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT INTO app_state (id, data, updated_at)
                            VALUES (1, $data, $updated)
                            ON CONFLICT(id) DO UPDATE SET data = $data, updated_at = $updated;
                        ";
                        cmd.Parameters.AddWithValue("$data", rawJson);
                        cmd.Parameters.AddWithValue("$updated", nowIso);
                        cmd.ExecuteNonQuery();

                        SyncAlternateDatabaseCopy(SqliteDbPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"SaveRawStateJson error: {ex.Message}");
                    }
                });
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private void SyncAlternateDatabaseCopy(string sourceDb)
        {
            try
            {
                string distDb = Path.Combine(BaseDir, "dist", "study_hub.db");
                if (!string.Equals(sourceDb, distDb, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(BaseDir, "dist")))
                {
                    File.Copy(sourceDb, distDb, true);
                }

                DirectoryInfo? parent = Directory.GetParent(BaseDir);
                if (parent != null && string.Equals(Path.GetFileName(BaseDir), "dist", StringComparison.OrdinalIgnoreCase))
                {
                    string parentDb = Path.Combine(parent.FullName, "study_hub.db");
                    File.Copy(sourceDb, parentDb, true);
                }
            }
            catch { }
        }

        private void SnapshotDailyBackup(string currentJson)
        {
            try
            {
                string todayFile = Path.Combine(BackupsDir, $"state_{DateTime.Now:yyyy-MM-dd}.json");
                if (!File.Exists(todayFile))
                {
                    File.WriteAllText(todayFile, currentJson, Encoding.UTF8);
                }

                var backups = Directory.GetFiles(BackupsDir, "state_*.json")
                    .OrderBy(f => f)
                    .ToList();
                while (backups.Count > 14)
                {
                    File.Delete(backups[0]);
                    backups.RemoveAt(0);
                }
            }
            catch { }
        }

        public StudyFileItem ImportStudyFile(string sourceFilePath, string subjectId, string folderName)
        {
            string origName = Path.GetFileName(sourceFilePath);
            string ext = Path.GetExtension(origName).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(origName);
            string safeStem = Regex.Replace(stem, @"[^A-Za-z0-9_\-]", "_");
            if (safeStem.Length > 60) safeStem = safeStem.Substring(0, 60);
            if (string.IsNullOrWhiteSpace(safeStem)) safeStem = "file";

            string hex = Guid.NewGuid().ToString("N").Substring(0, 8);
            string uniqueFilename = $"{subjectId}_{hex}_{safeStem}{ext}";
            string destPath = Path.Combine(FilesDir, uniqueFilename);

            File.Copy(sourceFilePath, destPath, true);

            // Also sync to dist/StudyHub_Files if running from root
            try
            {
                string distFiles = Path.Combine(BaseDir, "dist", "StudyHub_Files");
                if (Directory.Exists(distFiles) && !string.Equals(FilesDir, distFiles, StringComparison.OrdinalIgnoreCase))
                    File.Copy(destPath, Path.Combine(distFiles, uniqueFilename), true);
            }
            catch { }

            try
            {
                using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO study_files (filename, original_name, folder, subject_id)
                    VALUES ($fn, $orig, $fld, $sub);
                ";
                cmd.Parameters.AddWithValue("$fn", uniqueFilename);
                cmd.Parameters.AddWithValue("$orig", origName);
                cmd.Parameters.AddWithValue("$fld", folderName);
                cmd.Parameters.AddWithValue("$sub", subjectId);
                cmd.ExecuteNonQuery();
            }
            catch { }

            string nowUtc = DateTime.UtcNow.ToString("O");
            long fileSize = 0;
            try { fileSize = new FileInfo(destPath).Length; } catch { }
            string sha256 = ComputeFileSha256(destPath);

            return new StudyFileItem
            {
                Id = $"file_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{hex}",
                SubjectId = subjectId,
                OriginalName = origName,
                Filename = uniqueFilename,
                Folder = string.IsNullOrWhiteSpace(folderName) ? "Lectures" : folderName,
                Sha256 = sha256,
                FileSizeBytes = fileSize,
                UpdatedAtUtc = nowUtc
            };
        }

        public MediaTrackItem ImportMediaFile(string sourceFilePath, string subjectId, string title)
        {
            string origName = Path.GetFileName(sourceFilePath);
            string ext = Path.GetExtension(origName).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(origName);
            string safeStem = Regex.Replace(stem, @"[^A-Za-z0-9_\-]", "_");
            if (safeStem.Length > 60) safeStem = safeStem.Substring(0, 60);
            if (string.IsNullOrWhiteSpace(safeStem)) safeStem = "media";

            string hex = Guid.NewGuid().ToString("N").Substring(0, 8);
            string uniqueFilename = $"{subjectId}_{hex}_{safeStem}{ext}";
            string destPath = Path.Combine(FilesDir, uniqueFilename);

            File.Copy(sourceFilePath, destPath, true);

            try
            {
                string distFiles = Path.Combine(BaseDir, "dist", "StudyHub_Files");
                if (Directory.Exists(distFiles) && !string.Equals(FilesDir, distFiles, StringComparison.OrdinalIgnoreCase))
                    File.Copy(destPath, Path.Combine(distFiles, uniqueFilename), true);
            }
            catch { }

            string nowUtc = DateTime.UtcNow.ToString("O");
            long fileSize = 0;
            try { fileSize = new FileInfo(destPath).Length; } catch { }
            string sha256 = ComputeFileSha256(destPath);

            return new MediaTrackItem
            {
                Id = $"media_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{hex}",
                SubjectId = subjectId,
                Title = string.IsNullOrWhiteSpace(title) ? stem : title,
                Filename = uniqueFilename,
                Date = DateTime.Now.ToString("MMM dd, yyyy"),
                Duration = "",
                Sha256 = sha256,
                FileSizeBytes = fileSize,
                UpdatedAtUtc = nowUtc
            };
        }

        public string GetStudyFilePath(string filename)
        {
            string p1 = Path.Combine(FilesDir, filename);
            if (File.Exists(p1)) return p1;

            // Check fallback locations (parent or dist)
            string p2 = Path.Combine(BaseDir, "dist", "StudyHub_Files", filename);
            if (File.Exists(p2)) return p2;

            var parent = Directory.GetParent(BaseDir);
            if (parent != null)
            {
                string p3 = Path.Combine(parent.FullName, "StudyHub_Files", filename);
                if (File.Exists(p3)) return p3;
            }
            return p1;
        }

        public void DeleteStudyFile(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return;
            string safe = Path.GetFileName(filename);
            string p = Path.Combine(FilesDir, safe);
            try { if (File.Exists(p)) File.Delete(p); } catch { }

            try
            {
                using var conn = new SqliteConnection($"Data Source={SqliteDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM study_files WHERE filename = $fn;";
                cmd.Parameters.AddWithValue("$fn", safe);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public CustomSoundItem ImportCustomSound(string sourceFilePath)
        {
            string origName = Path.GetFileName(sourceFilePath);
            string ext = Path.GetExtension(origName).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(origName);
            string safeStem = Regex.Replace(stem, @"[^A-Za-z0-9_\-]", "_");
            if (safeStem.Length > 50) safeStem = safeStem.Substring(0, 50);
            if (string.IsNullOrWhiteSpace(safeStem)) safeStem = "sound";

            string hex = Guid.NewGuid().ToString("N").Substring(0, 8);
            string uniqueName = $"snd_{hex}_{safeStem}{ext}";
            string destPath = Path.Combine(SoundsDir, uniqueName);
            File.Copy(sourceFilePath, destPath, true);

            string nowUtc = DateTime.UtcNow.ToString("O");
            long fileSize = 0;
            try { fileSize = new FileInfo(destPath).Length; } catch { }
            string sha256 = ComputeFileSha256(destPath);

            return new CustomSoundItem
            {
                Id = $"custom_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Name = stem.Replace('_', ' ').Trim(),
                Filename = uniqueName,
                OriginalName = origName,
                Url = $"/sounds/{uniqueName}",
                AddedAt = DateTime.Now.ToString("O"),
                Sha256 = sha256,
                FileSizeBytes = fileSize,
                UpdatedAtUtc = nowUtc
            };
        }

        public string GetSoundFilePath(string filename)
        {
            string safe = Path.GetFileName(filename);
            string p1 = Path.Combine(SoundsDir, safe);
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine(BaseDir, "dist", "StudyHub_Sounds", safe);
            if (File.Exists(p2)) return p2;

            var parent = Directory.GetParent(BaseDir);
            if (parent != null)
            {
                string p3 = Path.Combine(parent.FullName, "StudyHub_Sounds", safe);
                if (File.Exists(p3)) return p3;
            }
            return p1;
        }

        public void DeleteSoundFile(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return;
            string p = Path.Combine(SoundsDir, Path.GetFileName(filename));
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        public string ResolveExportDir(AppStateData state, string? overrideDir = null)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(overrideDir)) candidates.Add(overrideDir.Trim());
            if (!string.IsNullOrWhiteSpace(state?.Settings?.DefaultSaveLocation))
                candidates.Add(state.Settings.DefaultSaveLocation.Trim());
            candidates.Add(DefaultExportDir);

            foreach (var dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string test = Path.Combine(dir, ".sh_write_test");
                    File.WriteAllText(test, "ok");
                    File.Delete(test);
                    return Path.GetFullPath(dir);
                }
                catch { }
            }
            return Path.GetFullPath(DefaultExportDir);
        }

        public string ExportBackupFile(AppStateData state, string? targetFolder = null)
        {
            string dir = ResolveExportDir(state, targetFolder);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string filename = $"StudyHub_Backup_{stamp}.json";
            string outPath = Path.Combine(dir, filename);
            string json = JsonSerializer.Serialize(state, _prettyJsonOptions);
            File.WriteAllText(outPath, json, Encoding.UTF8);
            return outPath;
        }

        public AppStateData? ImportBackupFile(string filePath)
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            var imported = JsonSerializer.Deserialize<AppStateData>(json, _jsonOptions);
            if (imported == null || imported.Subjects == null)
                throw new InvalidDataException("Invalid Study Hub backup file.");
            imported.Normalize();
            return imported;
        }

        public void ExecuteFactoryReset(AppStateData currentState)
        {
            try
            {
                string oldJson = JsonSerializer.Serialize(currentState, _jsonOptions);
                SnapshotDailyBackup(oldJson);
                ClearBaseSnapshot();
                lock (_trackLock)
                {
                    _hashManifest = new SyncEntityHashManifest();
                    SaveHashManifest(_hashManifest);
                }

                foreach (var dir in new[] { FilesDir, SoundsDir })
                {
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                }

                SetWindowsStartup(false);
            }
            catch { }
        }

        public bool GetWindowsStartupStatus()
        {
#if WINDOWS
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                var val = key?.GetValue("GraduationStudyHub") as string;
                return !string.IsNullOrWhiteSpace(val);
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public bool SetWindowsStartup(bool enabled)
        {
#if WINDOWS
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return false;
                if (enabled)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName
                        ?? Path.Combine(BaseDir, "StudyHub.exe");
                    key.SetValue("GraduationStudyHub", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("GraduationStudyHub", false);
                }
                return true;
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }
    }
}
