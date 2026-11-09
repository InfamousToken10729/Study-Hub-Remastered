using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
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

        private readonly SemaphoreSlim _saveLock = new(1, 1);
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

            Directory.CreateDirectory(FilesDir);
            Directory.CreateDirectory(SoundsDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(DefaultExportDir);
            Directory.CreateDirectory(WebViewDataDir);

            EnsureDatabaseTables();
        }

        private static string ResolveWritableDataDirectory()
        {
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
                            state.Normalize();
                            return state;
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
                        state.Normalize();
                        return state;
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
                        state.Normalize();
                        return state;
                    }
                }
            }
            catch { }

            var fresh = new AppStateData();
            fresh.Normalize();
            return fresh;
        }

        public async Task SaveStateAsync(AppStateData state)
        {
            if (state == null) return;
            await _saveLock.WaitAsync();
            try
            {
                await Task.Run(() => SaveStateSync(state));
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public void SaveStateSync(AppStateData state)
        {
            try
            {
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

            return new StudyFileItem
            {
                Id = $"file_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{hex}",
                SubjectId = subjectId,
                OriginalName = origName,
                Filename = uniqueFilename,
                Folder = string.IsNullOrWhiteSpace(folderName) ? "Lectures" : folderName
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

            return new MediaTrackItem
            {
                Id = $"media_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{hex}",
                SubjectId = subjectId,
                Title = string.IsNullOrWhiteSpace(title) ? stem : title,
                Filename = uniqueFilename,
                Date = DateTime.Now.ToString("MMM dd, yyyy"),
                Duration = ""
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

            return new CustomSoundItem
            {
                Id = $"custom_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Name = stem.Replace('_', ' ').Trim(),
                Filename = uniqueName,
                OriginalName = origName,
                Url = $"/sounds/{uniqueName}",
                AddedAt = DateTime.Now.ToString("O")
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
        }

        public bool SetWindowsStartup(bool enabled)
        {
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
        }
    }
}
