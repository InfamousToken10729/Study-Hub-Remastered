using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudyHub.Models;

namespace StudyHub.Services
{
    public sealed class RemoteBinaryFileMeta
    {
        public string DriveFileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Md5Checksum { get; set; } = "";
    }

    public sealed class LocalBinaryFileMeta
    {
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public static class SmartSyncMergeEngine
    {
        private static readonly JsonSerializerOptions CloneJsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> DefaultSeedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Mobile Communication",
            "Avionics"
        };

        public static AppStateData CloneState(AppStateData source)
        {
            var json = JsonSerializer.Serialize(source, CloneJsonOpts);
            var clone = JsonSerializer.Deserialize<AppStateData>(json, CloneJsonOpts) ?? new AppStateData();
            clone.Normalize();
            return clone;
        }

        public static string NormalizeUtc(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
            {
                return dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }
            return "";
        }

        public static DateTimeOffset ParseUtcOrMin(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.MinValue;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
            {
                return dto;
            }
            return DateTimeOffset.MinValue;
        }

        private static string MaxUtc(string? a, string? b)
        {
            var da = ParseUtcOrMin(a);
            var db = ParseUtcOrMin(b);
            if (da == DateTimeOffset.MinValue && db == DateTimeOffset.MinValue)
                return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return (da >= db ? da : db).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        public static string ComputeStringSha256(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input ?? "");
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static SyncDatasetCounts ComputeCounts(AppStateData? state)
        {
            if (state == null) return new SyncDatasetCounts();
            return new SyncDatasetCounts
            {
                Subjects = state.Subjects?.Count ?? 0,
                Pdfs = state.Pdfs?.Count ?? 0,
                Videos = state.Videos?.Count ?? 0,
                Recordings = state.Recordings?.Count ?? 0,
                Notes = state.Notes?.Count ?? 0,
                Tasks = state.Tasks?.Count ?? 0,
                Flashcards = state.Flashcards?.Count ?? 0,
                Whiteboards = state.Whiteboards?.Count ?? 0,
                Deadlines = state.Deadlines?.Count ?? 0,
                Links = state.Links?.Count ?? 0,
                CalendarEvents = state.CalendarEvents?.Count ?? 0,
                CustomSounds = state.CustomSounds?.Count ?? 0,
                SoundPresets = state.SavedSoundPresets?.Count ?? 0
            };
        }

        public static bool IsSubjectPopulated(string subjectId, AppStateData state)
        {
            if (string.IsNullOrWhiteSpace(subjectId) || state == null) return false;
            return
                state.Pdfs.Any(x => x.SubjectId == subjectId) ||
                state.Videos.Any(x => x.SubjectId == subjectId) ||
                state.Recordings.Any(x => x.SubjectId == subjectId) ||
                state.Notes.Any(x => x.SubjectId == subjectId) ||
                state.Tasks.Any(x => x.SubjectId == subjectId) ||
                state.Flashcards.Any(x => x.SubjectId == subjectId) ||
                state.Whiteboards.Any(x => x.SubjectId == subjectId) ||
                state.Deadlines.Any(x => x.SubjectId == subjectId) ||
                state.Links.Any(x => x.SubjectId == subjectId) ||
                state.CalendarEvents.Any(x => x.SubjectId == subjectId) ||
                state.SubjectTimes.GetValueOrDefault(subjectId, 0) > 1.0;
        }

        public static bool IsUntouchedDefaultSeedSubject(SubjectItem sub, AppStateData state)
        {
            if (sub == null) return false;
            bool looksLikeSeed =
                sub.Id == "sub_mobile_comm" ||
                sub.Id == "sub_avionics" ||
                (DefaultSeedNames.Contains((sub.Name ?? "").Trim()) && (sub.Id?.StartsWith("sub_", StringComparison.OrdinalIgnoreCase) ?? false));

            if (!looksLikeSeed) return false;
            return !sub.Archived && !IsSubjectPopulated(sub.Id ?? "", state);
        }

        public static bool HasRealUserContent(AppStateData? state)
        {
            if (state == null) return false;
            var counts = ComputeCounts(state);
            int nonSubjectItems = counts.TotalItems - counts.Subjects;
            if (nonSubjectItems > 0) return true;
            if (state.Activity.Values.Any(v => v > 0)) return true;
            if (state.PomodorosCompleted > 0) return true;
            return state.Subjects.Any(s => !IsUntouchedDefaultSeedSubject(s, state));
        }

        /// <summary>
        /// Automatically detects added, edited, and deleted entities relative to the previous saved manifest.
        /// Stamps UpdatedAtUtc on changed items and records SyncTombstone entries for deletions.
        /// </summary>
        public static void TrackLocalChanges(
            AppStateData state,
            SyncEntityHashManifest manifest,
            string deviceId,
            bool suppressTombstones = false)
        {
            state.Normalize();
            manifest.Normalize();

            string nowUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var currentMap = new Dictionary<string, (string Hash, string SubjectId, string UpdatedAt, Action<string> SetUpdatedAt)>();
            RegisterAllEntities(state, new DynamicRegisterHelper(currentMap));

            if (!manifest.Initialized)
            {
                manifest.Entities.Clear();
                foreach (var kv in currentMap)
                {
                    string ts = string.IsNullOrWhiteSpace(kv.Value.UpdatedAt) ? nowUtc : kv.Value.UpdatedAt;
                    kv.Value.SetUpdatedAt(ts);
                    manifest.Entities[kv.Key] = new SyncEntityEntry
                    {
                        Hash = kv.Value.Hash,
                        SubjectId = kv.Value.SubjectId,
                        UpdatedAtUtc = ts
                    };
                }
                manifest.Initialized = true;
                return;
            }

            foreach (var kv in currentMap)
            {
                var (hash, _, updatedAt, setUpdatedAt) = kv.Value;
                if (!manifest.Entities.TryGetValue(kv.Key, out var prev))
                {
                    string ts = string.IsNullOrWhiteSpace(updatedAt) ? nowUtc : NormalizeUtc(updatedAt);
                    if (string.IsNullOrWhiteSpace(ts)) ts = nowUtc;
                    setUpdatedAt(ts);

                    if (!suppressTombstones)
                    {
                        RemoveMatchingTombstone(state.SyncMeta.Tombstones, kv.Key);
                    }
                }
                else if (!string.Equals(prev.Hash, hash, StringComparison.Ordinal))
                {
                    string ts = suppressTombstones && !string.IsNullOrWhiteSpace(updatedAt)
                        ? NormalizeUtc(updatedAt)
                        : nowUtc;
                    if (string.IsNullOrWhiteSpace(ts)) ts = nowUtc;
                    setUpdatedAt(ts);
                }
                else if (string.IsNullOrWhiteSpace(updatedAt))
                {
                    string recovered = string.IsNullOrWhiteSpace(prev.UpdatedAtUtc) ? nowUtc : prev.UpdatedAtUtc;
                    setUpdatedAt(recovered);
                }
            }

            if (!suppressTombstones)
            {
                foreach (var prevKv in manifest.Entities)
                {
                    if (!currentMap.ContainsKey(prevKv.Key))
                    {
                        int sep = prevKv.Key.IndexOf(':');
                        if (sep > 0)
                        {
                            string eType = prevKv.Key[..sep];
                            string eId = prevKv.Key[(sep + 1)..];
                            UpsertTombstone(state.SyncMeta.Tombstones, new SyncTombstone
                            {
                                EntityType = eType,
                                EntityId = eId,
                                SubjectId = prevKv.Value.SubjectId ?? "",
                                DeletedAtUtc = nowUtc,
                                DeletedByDeviceId = deviceId ?? ""
                            });
                        }
                    }
                }
            }

            var updatedMap = new Dictionary<string, (string Hash, string SubjectId, string UpdatedAt, Action<string> SetUpdatedAt)>();
            RegisterAllEntities(state, new DynamicRegisterHelper(updatedMap));
            manifest.Entities.Clear();
            foreach (var kv in updatedMap)
            {
                manifest.Entities[kv.Key] = new SyncEntityEntry
                {
                    Hash = kv.Value.Hash,
                    SubjectId = kv.Value.SubjectId,
                    UpdatedAtUtc = string.IsNullOrWhiteSpace(kv.Value.UpdatedAt) ? nowUtc : kv.Value.UpdatedAt
                };
            }

            PruneTombstones(state.SyncMeta.Tombstones);
        }

        public static SyncMergeOutcome EvaluateAndMerge(
            AppStateData localRaw,
            AppStateData? remoteRaw,
            AppStateData? baseRaw,
            SyncResolutionMode mode,
            IReadOnlyList<SyncConflictItem>? resolvedConflicts,
            string deviceId,
            IReadOnlyDictionary<string, LocalBinaryFileMeta>? localFiles,
            IReadOnlyDictionary<string, RemoteBinaryFileMeta>? remoteFiles,
            IReadOnlyDictionary<string, LocalBinaryFileMeta>? localSounds,
            IReadOnlyDictionary<string, RemoteBinaryFileMeta>? remoteSounds,
            bool mismatchAcknowledged = false)
        {
            var local = CloneState(localRaw ?? new AppStateData());
            string nowUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            if (remoteRaw == null)
            {
                local.SyncMeta.LastMergedAtUtc = nowUtc;
                local.SyncMeta.LastMergedByDeviceId = deviceId ?? "";
                local.SyncMeta.LastSyncSummary = "Initial cloud upload";
                var initialOutcome = new SyncMergeOutcome
                {
                    MergedState = local,
                    StatusSummary = "Ready to initialize Google Drive cloud backup."
                };
                ComputeFileTransfers(initialOutcome, localFiles, remoteFiles, localSounds, remoteSounds, uploadsAllowed: true, downloadsAllowed: false);
                return initialOutcome;
            }

            var remote = CloneState(remoteRaw);
            var baseState = baseRaw != null ? CloneState(baseRaw) : null;

            var localCounts = ComputeCounts(local);
            var remoteCounts = ComputeCounts(remote);

            if (mode == SyncResolutionMode.ForcePullCloud)
            {
                var pulled = CloneState(remote);
                PreserveDeviceLocalSettings(local, pulled);
                pulled.SyncMeta.LastMergedAtUtc = nowUtc;
                pulled.SyncMeta.LastMergedByDeviceId = deviceId ?? "";
                pulled.SyncMeta.LastSyncSummary = "Restored from Google Drive cloud backup";

                var pullOutcome = new SyncMergeOutcome
                {
                    MergedState = pulled,
                    CloudChangesAppliedCount = remoteCounts.TotalItems,
                    StatusSummary = $"Restored {remoteCounts.Subjects} course(s) and {remoteCounts.TotalItems} item(s) from Google Drive."
                };
                ComputeFileTransfers(pullOutcome, localFiles, remoteFiles, localSounds, remoteSounds, uploadsAllowed: false, downloadsAllowed: true);
                return pullOutcome;
            }

            if (mode == SyncResolutionMode.ForcePushLocal)
            {
                var pushed = CloneState(local);
                pushed.SyncMeta.LastMergedAtUtc = nowUtc;
                pushed.SyncMeta.LastMergedByDeviceId = deviceId ?? "";
                pushed.SyncMeta.LastSyncSummary = "Force pushed from device to Google Drive";

                var pushOutcome = new SyncMergeOutcome
                {
                    MergedState = pushed,
                    LocalChangesCount = localCounts.TotalItems,
                    StatusSummary = $"Uploaded {localCounts.Subjects} course(s) and {localCounts.TotalItems} item(s) to Google Drive."
                };
                ComputeFileTransfers(pushOutcome, localFiles, remoteFiles, localSounds, remoteSounds, uploadsAllowed: true, downloadsAllowed: false);
                return pushOutcome;
            }

            // 3-Way Smart Merge / SmartMergeKeepDeleted
            bool ignoreDeletions = mode == SyncResolutionMode.SmartMergeKeepDeleted;

            if (baseState == null)
            {
                DeduplicateUntouchedDefaultSeeds(local, remote);
            }

            var localTombstones = BuildTombstoneMap(local.SyncMeta.Tombstones);
            var remoteTombstones = BuildTombstoneMap(remote.SyncMeta.Tombstones);
            var decisionMap = (resolvedConflicts ?? Array.Empty<SyncConflictItem>())
                .Where(c => !string.IsNullOrWhiteSpace(c.ConflictId))
                .GroupBy(c => c.ConflictId)
                .ToDictionary(g => g.Key, g => g.First());

            var conflicts = new List<SyncConflictItem>();
            var removedPopulatedSubjects = new List<string>();
            int removedFromLocal = 0;
            int removedFromRemote = 0;
            int localChangesCount = 0;
            int cloudChangesApplied = 0;

            var merged = new AppStateData
            {
                Theme = !HasRealUserContent(local) && HasRealUserContent(remote) ? remote.Theme : local.Theme,
                TimerDock = local.TimerDock,
                PomodorosCompleted = Math.Max(local.PomodorosCompleted, remote.PomodorosCompleted)
            };

            merged.Subjects = MergeCollection(
                "subject",
                local.Subjects,
                remote.Subjects,
                baseState?.Subjects,
                x => x.Id,
                x => x.UpdatedAtUtc,
                HashSubject,
                ignoreDeletions,
                localTombstones,
                remoteTombstones,
                decisionMap,
                conflicts,
                out var subStats,
                (SubjectItem loc, SubjectItem rem, SubjectItem? bas, out SubjectItem mergedItem, out SyncConflictItem? cf) => MergeSubject(loc, rem, bas, out mergedItem, out cf),
                (loc, rem, resolved) => ApplySubjectConflict(loc, rem, resolved),
                onDeletedLocal: s => { if (IsSubjectPopulated(s.Id, local)) removedPopulatedSubjects.Add(s.Name); },
                onDeletedRemote: s => { if (IsSubjectPopulated(s.Id, remote)) removedPopulatedSubjects.Add(s.Name); });
            removedFromLocal += subStats.RemovedLocal;
            removedFromRemote += subStats.RemovedRemote;
            localChangesCount += subStats.LocalWins;
            cloudChangesApplied += subStats.RemoteWins;

            merged.Pdfs = MergeSimpleCollection("pdf", local.Pdfs, remote.Pdfs, baseState?.Pdfs,
                x => x.Id, x => x.UpdatedAtUtc, HashStudyFile, ignoreDeletions, localTombstones, remoteTombstones,
                out var pdfStats, MergeStudyFileMetadata);
            removedFromLocal += pdfStats.RemovedLocal;
            removedFromRemote += pdfStats.RemovedRemote;
            localChangesCount += pdfStats.LocalWins;
            cloudChangesApplied += pdfStats.RemoteWins;

            merged.Videos = MergeSimpleCollection("video", local.Videos, remote.Videos, baseState?.Videos,
                x => x.Id, x => x.UpdatedAtUtc, HashMedia, ignoreDeletions, localTombstones, remoteTombstones,
                out var vidStats, MergeMediaMetadata);
            removedFromLocal += vidStats.RemovedLocal;
            removedFromRemote += vidStats.RemovedRemote;
            localChangesCount += vidStats.LocalWins;
            cloudChangesApplied += vidStats.RemoteWins;

            merged.Recordings = MergeSimpleCollection("recording", local.Recordings, remote.Recordings, baseState?.Recordings,
                x => x.Id, x => x.UpdatedAtUtc, HashMedia, ignoreDeletions, localTombstones, remoteTombstones,
                out var recStats, MergeMediaMetadata);
            removedFromLocal += recStats.RemovedLocal;
            removedFromRemote += recStats.RemovedRemote;
            localChangesCount += recStats.LocalWins;
            cloudChangesApplied += recStats.RemoteWins;

            merged.Notes = MergeCollection("note", local.Notes, remote.Notes, baseState?.Notes,
                x => x.Id, x => x.UpdatedAtUtc, HashNote, ignoreDeletions, localTombstones, remoteTombstones,
                decisionMap, conflicts, out var noteStats,
                (NoteItem loc, NoteItem rem, NoteItem? bas, out NoteItem mergedItem, out SyncConflictItem? cf) => MergeNote(loc, rem, bas, local, remote, out mergedItem, out cf),
                ApplyNoteConflict);
            removedFromLocal += noteStats.RemovedLocal;
            removedFromRemote += noteStats.RemovedRemote;
            localChangesCount += noteStats.LocalWins;
            cloudChangesApplied += noteStats.RemoteWins;

            merged.Tasks = MergeCollection("task", local.Tasks, remote.Tasks, baseState?.Tasks,
                x => x.Id, x => x.UpdatedAtUtc, HashTask, ignoreDeletions, localTombstones, remoteTombstones,
                decisionMap, conflicts, out var taskStats,
                (TaskItem loc, TaskItem rem, TaskItem? bas, out TaskItem mergedItem, out SyncConflictItem? cf) => MergeTask(loc, rem, bas, local, remote, out mergedItem, out cf),
                ApplyTaskConflict);
            removedFromLocal += taskStats.RemovedLocal;
            removedFromRemote += taskStats.RemovedRemote;
            localChangesCount += taskStats.LocalWins;
            cloudChangesApplied += taskStats.RemoteWins;

            merged.Flashcards = MergeCollection("flashcard", local.Flashcards, remote.Flashcards, baseState?.Flashcards,
                x => x.Id, x => x.UpdatedAtUtc, HashFlashcard, ignoreDeletions, localTombstones, remoteTombstones,
                decisionMap, conflicts, out var fcStats,
                (FlashcardItem loc, FlashcardItem rem, FlashcardItem? bas, out FlashcardItem mergedItem, out SyncConflictItem? cf) => MergeFlashcard(loc, rem, bas, local, remote, out mergedItem, out cf),
                ApplyFlashcardConflict);
            removedFromLocal += fcStats.RemovedLocal;
            removedFromRemote += fcStats.RemovedRemote;
            localChangesCount += fcStats.LocalWins;
            cloudChangesApplied += fcStats.RemoteWins;

            merged.Whiteboards = MergeSimpleCollection("whiteboard", local.Whiteboards, remote.Whiteboards, baseState?.Whiteboards,
                x => x.Id, x => x.UpdatedAtUtc, HashWhiteboard, ignoreDeletions, localTombstones, remoteTombstones,
                out var wbStats, MergeWhiteboardLatestOrUnion);
            removedFromLocal += wbStats.RemovedLocal;
            removedFromRemote += wbStats.RemovedRemote;
            localChangesCount += wbStats.LocalWins;
            cloudChangesApplied += wbStats.RemoteWins;

            merged.Deadlines = MergeCollection("deadline", local.Deadlines, remote.Deadlines, baseState?.Deadlines,
                x => x.Id, x => x.UpdatedAtUtc, HashDeadline, ignoreDeletions, localTombstones, remoteTombstones,
                decisionMap, conflicts, out var dlStats,
                (DeadlineItem loc, DeadlineItem rem, DeadlineItem? bas, out DeadlineItem mergedItem, out SyncConflictItem? cf) => MergeDeadline(loc, rem, bas, local, remote, out mergedItem, out cf),
                ApplyDeadlineConflict);
            removedFromLocal += dlStats.RemovedLocal;
            removedFromRemote += dlStats.RemovedRemote;
            localChangesCount += dlStats.LocalWins;
            cloudChangesApplied += dlStats.RemoteWins;

            merged.Links = MergeCollection("link", local.Links, remote.Links, baseState?.Links,
                x => x.Id, x => x.UpdatedAtUtc, HashLink, ignoreDeletions, localTombstones, remoteTombstones,
                decisionMap, conflicts, out var linkStats,
                (LinkItem loc, LinkItem rem, LinkItem? bas, out LinkItem mergedItem, out SyncConflictItem? cf) => MergeLink(loc, rem, bas, local, remote, out mergedItem, out cf),
                ApplyLinkConflict);
            removedFromLocal += linkStats.RemovedLocal;
            removedFromRemote += linkStats.RemovedRemote;
            localChangesCount += linkStats.LocalWins;
            cloudChangesApplied += linkStats.RemoteWins;

            merged.CalendarEvents = MergeCollection("calendar_event", local.CalendarEvents, remote.CalendarEvents, baseState?.CalendarEvents,
                x => x.Id, x => x.UpdatedAtUtc, HashCalendarEvent, ignoreDeletions, localTombstones, remoteTombstones,
                decisionMap, conflicts, out var calStats,
                (CalendarEventItem loc, CalendarEventItem rem, CalendarEventItem? bas, out CalendarEventItem mergedItem, out SyncConflictItem? cf) => MergeCalendarEvent(loc, rem, bas, local, remote, out mergedItem, out cf),
                ApplyCalendarEventConflict);
            removedFromLocal += calStats.RemovedLocal;
            removedFromRemote += calStats.RemovedRemote;
            localChangesCount += calStats.LocalWins;
            cloudChangesApplied += calStats.RemoteWins;

            merged.CustomSounds = MergeSimpleCollection("custom_sound", local.CustomSounds, remote.CustomSounds, baseState?.CustomSounds,
                x => x.Id, x => x.UpdatedAtUtc, HashCustomSound, ignoreDeletions, localTombstones, remoteTombstones,
                out var csStats, (loc, rem) => ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc);
            removedFromLocal += csStats.RemovedLocal;
            removedFromRemote += csStats.RemovedRemote;
            localChangesCount += csStats.LocalWins;
            cloudChangesApplied += csStats.RemoteWins;

            merged.SavedSoundPresets = MergeSimpleCollection("sound_preset", local.SavedSoundPresets, remote.SavedSoundPresets, baseState?.SavedSoundPresets,
                x => x.Id, x => x.UpdatedAtUtc, HashSoundPreset, ignoreDeletions, localTombstones, remoteTombstones,
                out var spStats, (loc, rem) => ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc);
            removedFromLocal += spStats.RemovedLocal;
            removedFromRemote += spStats.RemovedRemote;
            localChangesCount += spStats.LocalWins;
            cloudChangesApplied += spStats.RemoteWins;

            // Merge monotonic dictionaries and sets
            merged.Activity = MergeIntMaxDict(local.Activity, remote.Activity);
            merged.PomodoroDays = MergeIntMaxDict(local.PomodoroDays, remote.PomodoroDays);
            merged.SubjectTimes = MergeDoubleMaxDict(local.SubjectTimes, remote.SubjectTimes);
            merged.PdfProgress = MergeIntMaxDict(local.PdfProgress, remote.PdfProgress);
            merged.FiredNotificationKeys = local.FiredNotificationKeys
                .Concat(remote.FiredNotificationKeys)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Merge Tombstones (unless recovering deleted items, in which case purge tombstones of recovered items)
            var mergedTombstones = MergeTombstones(local.SyncMeta.Tombstones, remote.SyncMeta.Tombstones);
            if (ignoreDeletions)
            {
                var presentKeys = new HashSet<string>(StringComparer.Ordinal);
                var helperMap = new Dictionary<string, (string Hash, string SubjectId, string UpdatedAt, Action<string> SetUpdatedAt)>();
                RegisterAllEntities(merged, new DynamicRegisterHelper(helperMap));
                foreach (var k in helperMap.Keys) presentKeys.Add(k);
                mergedTombstones = mergedTombstones
                    .Where(t => !presentKeys.Contains($"{t.EntityType}:{t.EntityId}"))
                    .ToList();
            }

            PruneTombstones(mergedTombstones);
            merged.SyncMeta = new AppSyncMetadata
            {
                SchemaVersion = 1,
                LastMergedAtUtc = nowUtc,
                LastMergedByDeviceId = deviceId ?? "",
                LastSyncSummary = $"Merged ({cloudChangesApplied} cloud updates, {localChangesCount} local updates)",
                Tombstones = mergedTombstones
            };

            merged.Settings = MergeSettings(local, remote, baseState);
            merged.CurrentSubjectId = !string.IsNullOrWhiteSpace(local.CurrentSubjectId) && merged.Subjects.Any(s => s.Id == local.CurrentSubjectId && !s.Archived)
                ? local.CurrentSubjectId
                : null;
            merged.Normalize();

            var mismatch = BuildMismatchReport(
                local, remote, baseState, mode, mismatchAcknowledged,
                localCounts, remoteCounts, removedFromLocal, removedFromRemote, removedPopulatedSubjects);

            var outcome = new SyncMergeOutcome
            {
                MergedState = merged,
                MismatchReport = mismatch,
                Conflicts = conflicts,
                LocalChangesCount = localChangesCount,
                CloudChangesAppliedCount = cloudChangesApplied,
                StatusSummary = mismatch.RequiresConfirmation
                    ? mismatch.ReasonTitle
                    : (conflicts.Count > 0
                        ? $"{conflicts.Count} conflict(s) need your review before saving."
                        : $"Smart Merge complete (+{cloudChangesApplied} cloud, +{localChangesCount} local).")
            };

            ComputeFileTransfers(outcome, localFiles, remoteFiles, localSounds, remoteSounds, uploadsAllowed: true, downloadsAllowed: true);
            return outcome;
        }

        private static SyncMismatchReport BuildMismatchReport(
            AppStateData local,
            AppStateData remote,
            AppStateData? baseState,
            SyncResolutionMode mode,
            bool mismatchAcknowledged,
            SyncDatasetCounts localCounts,
            SyncDatasetCounts remoteCounts,
            int removedFromLocal,
            int removedFromRemote,
            List<string> removedPopulatedSubjects)
        {
            var report = new SyncMismatchReport
            {
                LocalCounts = localCounts,
                CloudCounts = remoteCounts,
                WouldRemoveItemsCount = removedFromLocal + removedFromRemote,
                WouldRemoveSubjectsCount = removedPopulatedSubjects.Count,
                WouldRemoveSubjectNames = removedPopulatedSubjects.Distinct().ToList(),
                IsFirstSync = baseState == null
            };

            if (mismatchAcknowledged || mode != SyncResolutionMode.SmartMerge)
                return report;

            if (baseState == null && remoteCounts.TotalItems > 0)
            {
                bool localReal = HasRealUserContent(local);
                report.RequiresConfirmation = true;
                report.ReasonTitle = localReal
                    ? "First Sync: Data Found on Both Devices"
                    : "Existing Google Drive Backup Found";
                report.ReasonDescription = localReal
                    ? $"This device has {localCounts.Subjects} course(s) / {localCounts.TotalItems} item(s), while Google Drive already stores {remoteCounts.Subjects} course(s) / {remoteCounts.TotalItems} item(s). Choose how to unify them safely."
                    : $"This device is fresh, while Google Drive already stores {remoteCounts.Subjects} course(s) / {remoteCounts.TotalItems} item(s). Choose whether to restore/merge Cloud data safely without overwriting it.";
                return report;
            }

            bool largeLocalDrop = removedFromLocal >= 3 && removedFromLocal * 4 >= Math.Max(1, localCounts.TotalItems);
            bool largeRemoteDrop = removedFromRemote >= 3 && removedFromRemote * 4 >= Math.Max(1, remoteCounts.TotalItems);

            if (removedPopulatedSubjects.Count > 0 || largeLocalDrop || largeRemoteDrop)
            {
                report.RequiresConfirmation = true;
                report.IsLargeDeletionWarning = true;
                report.ReasonTitle = removedPopulatedSubjects.Count > 0
                    ? "Guardrail: Populated Course Deletion Detected"
                    : "Guardrail: Large Sync Deletion Detected";

                string courseNames = removedPopulatedSubjects.Count > 0
                    ? $" ({string.Join(", ", removedPopulatedSubjects.Distinct())})"
                    : "";
                report.ReasonDescription =
                    $"Syncing would delete {report.WouldRemoveItemsCount} item(s){courseNames} based on delete markers from offline edits or backup mismatch. Review and confirm before any deletion runs, or keep all items.";
            }

            return report;
        }

        private static void DeduplicateUntouchedDefaultSeeds(AppStateData local, AppStateData remote)
        {
            bool localHasReal = HasRealUserContent(local);
            bool remoteHasReal = HasRealUserContent(remote);

            // If local only has auto-seeded starter subjects while remote has real curriculum, suppress untouched starter clones
            if (!localHasReal && remoteHasReal)
            {
                local.Subjects.RemoveAll(s => IsUntouchedDefaultSeedSubject(s, local));
                return;
            }
            if (!remoteHasReal && localHasReal)
            {
                remote.Subjects.RemoveAll(s => IsUntouchedDefaultSeedSubject(s, remote));
                return;
            }

            var remoteByNormName = remote.Subjects
                .GroupBy(s => (s.Name ?? "").Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            local.Subjects.RemoveAll(ls =>
                IsUntouchedDefaultSeedSubject(ls, local) &&
                remoteByNormName.TryGetValue((ls.Name ?? "").Trim().ToLowerInvariant(), out var rs) &&
                !string.Equals(ls.Id, rs.Id, StringComparison.Ordinal));
        }

        private delegate bool TryAutoMergeDelegate<T>(T local, T remote, T? baseItem, out T merged, out SyncConflictItem? conflict);

        private struct CollectionStats
        {
            public int RemovedLocal;
            public int RemovedRemote;
            public int LocalWins;
            public int RemoteWins;
        }

        private static List<T> MergeSimpleCollection<T>(
            string entityType,
            List<T> localList,
            List<T> remoteList,
            List<T>? baseList,
            Func<T, string> getId,
            Func<T, string> getUpdatedAt,
            Func<T, string> getHash,
            bool ignoreDeletions,
            IReadOnlyDictionary<string, SyncTombstone> localTombstones,
            IReadOnlyDictionary<string, SyncTombstone> remoteTombstones,
            out CollectionStats stats,
            Func<T, T, T> mergeBoth)
        {
            var dummyDecisions = new Dictionary<string, SyncConflictItem>();
            var dummyConflicts = new List<SyncConflictItem>();
            return MergeCollection(
                entityType, localList, remoteList, baseList,
                getId, getUpdatedAt, getHash, ignoreDeletions,
                localTombstones, remoteTombstones, dummyDecisions, dummyConflicts,
                out stats,
                (T loc, T rem, T? bas, out T merged, out SyncConflictItem? conflict) =>
                {
                    conflict = null;
                    if (bas != null)
                    {
                        bool locChanged = getHash(loc) != getHash(bas);
                        bool remChanged = getHash(rem) != getHash(bas);
                        if (!locChanged && remChanged) { merged = rem; return true; }
                        if (locChanged && !remChanged) { merged = loc; return true; }
                    }
                    merged = mergeBoth(loc, rem);
                    return true;
                },
                (loc, rem, _) => mergeBoth(loc, rem));
        }

        private static List<T> MergeCollection<T>(
            string entityType,
            List<T> localList,
            List<T> remoteList,
            List<T>? baseList,
            Func<T, string> getId,
            Func<T, string> getUpdatedAt,
            Func<T, string> getHash,
            bool ignoreDeletions,
            IReadOnlyDictionary<string, SyncTombstone> localTombstones,
            IReadOnlyDictionary<string, SyncTombstone> remoteTombstones,
            IReadOnlyDictionary<string, SyncConflictItem> decisionMap,
            List<SyncConflictItem> conflictsOut,
            out CollectionStats stats,
            TryAutoMergeDelegate<T> tryAutoMerge,
            Func<T, T, SyncConflictItem, T> applyConflict,
            Action<T>? onDeletedLocal = null,
            Action<T>? onDeletedRemote = null)
        {
            stats = default;
            var localMap = ToIdDict(localList, getId);
            var remoteMap = ToIdDict(remoteList, getId);
            var baseMap = baseList != null ? ToIdDict(baseList, getId) : new Dictionary<string, T>(StringComparer.Ordinal);

            var allIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in localList)
            {
                string id = getId(item);
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) allIds.Add(id);
            }
            foreach (var item in remoteList)
            {
                string id = getId(item);
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) allIds.Add(id);
            }

            var result = new List<T>();

            foreach (var id in allIds)
            {
                bool hasLocal = localMap.TryGetValue(id, out var loc);
                bool hasRemote = remoteMap.TryGetValue(id, out var rem);
                bool hasBase = baseMap.TryGetValue(id, out var bas);
                string key = $"{entityType}:{id}";

                if (hasLocal && hasRemote && loc != null && rem != null)
                {
                    if (getHash(loc) == getHash(rem))
                    {
                        result.Add(ParseUtcOrMin(getUpdatedAt(rem)) > ParseUtcOrMin(getUpdatedAt(loc)) ? rem : loc);
                        continue;
                    }

                    if (hasBase && bas != null)
                    {
                        bool locChanged = getHash(loc) != getHash(bas);
                        bool remChanged = getHash(rem) != getHash(bas);
                        if (!locChanged && remChanged)
                        {
                            result.Add(rem);
                            stats.RemoteWins++;
                            continue;
                        }
                        if (locChanged && !remChanged)
                        {
                            result.Add(loc);
                            stats.LocalWins++;
                            continue;
                        }
                    }

                    if (decisionMap.TryGetValue(key, out var resolved) && resolved.Choice != SyncConflictChoice.Pending)
                    {
                        result.Add(applyConflict(loc, rem, resolved));
                        stats.LocalWins++;
                        continue;
                    }

                    if (tryAutoMerge(loc, rem, bas, out var mergedItem, out var conflict) && conflict == null)
                    {
                        result.Add(mergedItem);
                    }
                    else if (conflict != null)
                    {
                        conflictsOut.Add(conflict);
                        result.Add(loc); // placeholder until resolved
                    }
                    else
                    {
                        result.Add(mergedItem);
                    }
                }
                else if (hasLocal && loc != null)
                {
                    if (!ignoreDeletions && remoteTombstones.TryGetValue(key, out var remTomb))
                    {
                        bool editedOfflineLocally = hasBase && bas != null && getHash(loc) != getHash(bas);
                        bool localNewerThanDelete = ParseUtcOrMin(getUpdatedAt(loc)) > ParseUtcOrMin(remTomb.DeletedAtUtc);

                        if (editedOfflineLocally || localNewerThanDelete)
                        {
                            // Edit-vs-Delete protection: never drop an offline edit silently
                            result.Add(loc);
                            stats.LocalWins++;
                        }
                        else
                        {
                            stats.RemovedLocal++;
                            onDeletedLocal?.Invoke(loc);
                        }
                    }
                    else
                    {
                        result.Add(loc);
                        if (!hasBase) stats.LocalWins++;
                    }
                }
                else if (hasRemote && rem != null)
                {
                    if (!ignoreDeletions && localTombstones.TryGetValue(key, out var locTomb))
                    {
                        bool editedOfflineRemotely = hasBase && bas != null && getHash(rem) != getHash(bas);
                        bool remoteNewerThanDelete = ParseUtcOrMin(getUpdatedAt(rem)) > ParseUtcOrMin(locTomb.DeletedAtUtc);

                        if (editedOfflineRemotely || remoteNewerThanDelete)
                        {
                            result.Add(rem);
                            stats.RemoteWins++;
                        }
                        else
                        {
                            stats.RemovedRemote++;
                            onDeletedRemote?.Invoke(rem);
                        }
                    }
                    else
                    {
                        result.Add(rem);
                        stats.RemoteWins++;
                    }
                }
            }

            return result;
        }

        private static bool MergeSubject(SubjectItem loc, SubjectItem rem, SubjectItem? bas, out SubjectItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            var unionFolders = (loc.Folders ?? new List<string>())
                .Concat(rem.Folders ?? new List<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool sameCore =
                string.Equals(loc.Name, rem.Name, StringComparison.Ordinal) &&
                string.Equals(loc.Icon, rem.Icon, StringComparison.Ordinal) &&
                string.Equals(loc.Color, rem.Color, StringComparison.Ordinal) &&
                loc.Archived == rem.Archived;

            if (sameCore)
            {
                merged = new SubjectItem
                {
                    Id = loc.Id,
                    Name = loc.Name,
                    Icon = loc.Icon,
                    Color = loc.Color,
                    Archived = loc.Archived,
                    Folders = unionFolders,
                    UpdatedAtUtc = MaxUtc(loc.UpdatedAtUtc, rem.UpdatedAtUtc)
                };
                return true;
            }

            merged = loc;
            conflict = new SyncConflictItem
            {
                ConflictId = $"subject:{loc.Id}",
                EntityType = SyncConflictType.SubjectDetails,
                EntityId = loc.Id,
                Title = $"Course: {loc.Name}",
                LocalSummary = $"{loc.Name} ({loc.Icon}{(loc.Archived ? ", Archived" : "")})",
                CloudSummary = $"{rem.Name} ({rem.Icon}{(rem.Archived ? ", Archived" : "")})",
                LocalText = $"Name: {loc.Name}\nIcon: {loc.Icon}\nArchived: {loc.Archived}",
                CloudText = $"Name: {rem.Name}\nIcon: {rem.Icon}\nArchived: {rem.Archived}",
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = true
            };
            return false;
        }

        private static SubjectItem ApplySubjectConflict(SubjectItem loc, SubjectItem rem, SyncConflictItem resolved)
        {
            var unionFolders = (loc.Folders ?? new List<string>())
                .Concat(rem.Folders ?? new List<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool remNewer = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc);
            SubjectItem chosen = resolved.Choice switch
            {
                SyncConflictChoice.KeepCloud => new SubjectItem
                {
                    Id = rem.Id, Name = rem.Name, Icon = rem.Icon, Color = rem.Color, Archived = rem.Archived,
                    Folders = unionFolders, UpdatedAtUtc = DateTime.UtcNow.ToString("O")
                },
                SyncConflictChoice.CombineBoth => new SubjectItem
                {
                    Id = loc.Id,
                    Name = string.Equals(loc.Name, rem.Name, StringComparison.OrdinalIgnoreCase) ? loc.Name : $"{loc.Name} / {rem.Name}",
                    Icon = remNewer ? rem.Icon : loc.Icon,
                    Color = remNewer ? rem.Color : loc.Color,
                    Archived = loc.Archived && rem.Archived,
                    Folders = unionFolders,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("O")
                },
                _ => new SubjectItem
                {
                    Id = loc.Id, Name = loc.Name, Icon = loc.Icon, Color = loc.Color, Archived = loc.Archived,
                    Folders = unionFolders, UpdatedAtUtc = DateTime.UtcNow.ToString("O")
                }
            };
            return chosen;
        }

        private static bool MergeNote(NoteItem loc, NoteItem rem, NoteItem? bas, AppStateData localState, AppStateData remoteState, out NoteItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            string locTrim = (loc.Text ?? "").Trim();
            string remTrim = (rem.Text ?? "").Trim();
            if (string.Equals(locTrim, remTrim, StringComparison.Ordinal))
            {
                merged = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
                return true;
            }
            if (string.IsNullOrEmpty(locTrim)) { merged = rem; return true; }
            if (string.IsNullOrEmpty(remTrim)) { merged = loc; return true; }

            // Non-destructive strict-superset auto-merge when user appended lines on one device
            if (remTrim.Contains(locTrim, StringComparison.Ordinal)) { merged = rem; return true; }
            if (locTrim.Contains(remTrim, StringComparison.Ordinal)) { merged = loc; return true; }

            merged = loc;
            string subjName = FindSubjectName(loc.SubjectId, localState, remoteState);
            conflict = new SyncConflictItem
            {
                ConflictId = $"note:{loc.Id}",
                EntityType = SyncConflictType.NoteContent,
                EntityId = loc.Id,
                Title = $"Note in {subjName}",
                LocalSummary = TruncatePreview(loc.Text, 80),
                CloudSummary = TruncatePreview(rem.Text, 80),
                LocalText = loc.Text,
                CloudText = rem.Text,
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = true
            };
            return false;
        }

        private static NoteItem ApplyNoteConflict(NoteItem loc, NoteItem rem, SyncConflictItem resolved)
        {
            string now = DateTime.UtcNow.ToString("O");
            return resolved.Choice switch
            {
                SyncConflictChoice.KeepCloud => new NoteItem
                {
                    Id = rem.Id, SubjectId = rem.SubjectId, Text = rem.Text, Date = rem.Date, UpdatedAtUtc = now
                },
                SyncConflictChoice.CombineBoth => new NoteItem
                {
                    Id = loc.Id,
                    SubjectId = string.IsNullOrWhiteSpace(loc.SubjectId) ? rem.SubjectId : loc.SubjectId,
                    Date = string.IsNullOrWhiteSpace(loc.Date) ? rem.Date : loc.Date,
                    Text = $"{loc.Text?.TrimEnd()}\n\n--- [Cloud Version Added via Sync] ---\n{rem.Text?.TrimStart()}",
                    UpdatedAtUtc = now
                },
                _ => new NoteItem
                {
                    Id = loc.Id, SubjectId = loc.SubjectId, Text = loc.Text, Date = loc.Date, UpdatedAtUtc = now
                }
            };
        }

        private static bool MergeTask(TaskItem loc, TaskItem rem, TaskItem? bas, AppStateData localState, AppStateData remoteState, out TaskItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            if (string.Equals(loc.Text, rem.Text, StringComparison.Ordinal))
            {
                merged = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
                return true;
            }
            merged = loc;
            conflict = new SyncConflictItem
            {
                ConflictId = $"task:{loc.Id}",
                EntityType = SyncConflictType.TaskItem,
                EntityId = loc.Id,
                Title = $"Task in {FindSubjectName(loc.SubjectId, localState, remoteState)}",
                LocalSummary = loc.Text,
                CloudSummary = rem.Text,
                LocalText = $"{loc.Text} (Done: {loc.Completed})",
                CloudText = $"{rem.Text} (Done: {rem.Completed})",
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = true
            };
            return false;
        }

        private static TaskItem ApplyTaskConflict(TaskItem loc, TaskItem rem, SyncConflictItem resolved)
        {
            string now = DateTime.UtcNow.ToString("O");
            return resolved.Choice switch
            {
                SyncConflictChoice.KeepCloud => new TaskItem { Id = rem.Id, SubjectId = rem.SubjectId, Text = rem.Text, Completed = rem.Completed, UpdatedAtUtc = now },
                SyncConflictChoice.CombineBoth => new TaskItem { Id = loc.Id, SubjectId = loc.SubjectId, Text = $"{loc.Text} / {rem.Text}", Completed = loc.Completed || rem.Completed, UpdatedAtUtc = now },
                _ => new TaskItem { Id = loc.Id, SubjectId = loc.SubjectId, Text = loc.Text, Completed = loc.Completed, UpdatedAtUtc = now }
            };
        }

        private static bool MergeFlashcard(FlashcardItem loc, FlashcardItem rem, FlashcardItem? bas, AppStateData localState, AppStateData remoteState, out FlashcardItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            merged = loc;
            conflict = new SyncConflictItem
            {
                ConflictId = $"flashcard:{loc.Id}",
                EntityType = SyncConflictType.Flashcard,
                EntityId = loc.Id,
                Title = $"Flashcard ({FindSubjectName(loc.SubjectId, localState, remoteState)})",
                LocalSummary = TruncatePreview(loc.Question, 80),
                CloudSummary = TruncatePreview(rem.Question, 80),
                LocalText = $"Q: {loc.Question}\nA: {loc.Answer}",
                CloudText = $"Q: {rem.Question}\nA: {rem.Answer}",
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = true
            };
            return false;
        }

        private static FlashcardItem ApplyFlashcardConflict(FlashcardItem loc, FlashcardItem rem, SyncConflictItem resolved)
        {
            string now = DateTime.UtcNow.ToString("O");
            return resolved.Choice switch
            {
                SyncConflictChoice.KeepCloud => new FlashcardItem { Id = rem.Id, SubjectId = rem.SubjectId, Type = rem.Type, Question = rem.Question, Answer = rem.Answer, UpdatedAtUtc = now },
                SyncConflictChoice.CombineBoth => new FlashcardItem
                {
                    Id = loc.Id,
                    SubjectId = loc.SubjectId,
                    Type = loc.Type,
                    Question = string.Equals(loc.Question, rem.Question, StringComparison.Ordinal) ? loc.Question : $"{loc.Question} / {rem.Question}",
                    Answer = string.Equals(loc.Answer, rem.Answer, StringComparison.Ordinal) ? loc.Answer : $"{loc.Answer}\n---\n{rem.Answer}",
                    UpdatedAtUtc = now
                },
                _ => new FlashcardItem { Id = loc.Id, SubjectId = loc.SubjectId, Type = loc.Type, Question = loc.Question, Answer = loc.Answer, UpdatedAtUtc = now }
            };
        }

        private static bool MergeDeadline(DeadlineItem loc, DeadlineItem rem, DeadlineItem? bas, AppStateData localState, AppStateData remoteState, out DeadlineItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            bool onlyCompletionDiff =
                string.Equals(loc.Title, rem.Title, StringComparison.Ordinal) &&
                string.Equals(loc.Date, rem.Date, StringComparison.Ordinal) &&
                string.Equals(loc.Time, rem.Time, StringComparison.Ordinal) &&
                string.Equals(loc.Notes, rem.Notes, StringComparison.Ordinal);

            if (onlyCompletionDiff)
            {
                merged = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
                return true;
            }

            merged = loc;
            conflict = new SyncConflictItem
            {
                ConflictId = $"deadline:{loc.Id}",
                EntityType = SyncConflictType.Deadline,
                EntityId = loc.Id,
                Title = $"Deadline: {loc.Title} ({FindSubjectName(loc.SubjectId, localState, remoteState)})",
                LocalSummary = $"{loc.Title} @ {loc.Date} {loc.Time}",
                CloudSummary = $"{rem.Title} @ {rem.Date} {rem.Time}",
                LocalText = $"Title: {loc.Title}\nDue: {loc.Date} {loc.Time}\nNotes: {loc.Notes}",
                CloudText = $"Title: {rem.Title}\nDue: {rem.Date} {rem.Time}\nNotes: {rem.Notes}",
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = true
            };
            return false;
        }

        private static DeadlineItem ApplyDeadlineConflict(DeadlineItem loc, DeadlineItem rem, SyncConflictItem resolved)
        {
            string now = DateTime.UtcNow.ToString("O");
            bool remNewer = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc);
            return resolved.Choice switch
            {
                SyncConflictChoice.KeepCloud => new DeadlineItem
                {
                    Id = rem.Id, SubjectId = rem.SubjectId, Title = rem.Title, Type = rem.Type, Date = rem.Date, Time = rem.Time,
                    Completed = rem.Completed, CompletedAt = rem.CompletedAt, Notes = rem.Notes, UpdatedAtUtc = now
                },
                SyncConflictChoice.CombineBoth => new DeadlineItem
                {
                    Id = loc.Id,
                    SubjectId = loc.SubjectId,
                    Title = string.Equals(loc.Title, rem.Title, StringComparison.Ordinal) ? loc.Title : $"{loc.Title} / {rem.Title}",
                    Type = remNewer ? rem.Type : loc.Type,
                    Date = remNewer ? rem.Date : loc.Date,
                    Time = remNewer ? rem.Time : loc.Time,
                    Completed = loc.Completed || rem.Completed,
                    CompletedAt = loc.CompletedAt ?? rem.CompletedAt,
                    Notes = CombineTwoTexts(loc.Notes, rem.Notes),
                    UpdatedAtUtc = now
                },
                _ => new DeadlineItem
                {
                    Id = loc.Id, SubjectId = loc.SubjectId, Title = loc.Title, Type = loc.Type, Date = loc.Date, Time = loc.Time,
                    Completed = loc.Completed, CompletedAt = loc.CompletedAt, Notes = loc.Notes, UpdatedAtUtc = now
                }
            };
        }

        private static bool MergeLink(LinkItem loc, LinkItem rem, LinkItem? bas, AppStateData localState, AppStateData remoteState, out LinkItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            merged = loc;
            conflict = new SyncConflictItem
            {
                ConflictId = $"link:{loc.Id}",
                EntityType = SyncConflictType.Link,
                EntityId = loc.Id,
                Title = $"Link in {FindSubjectName(loc.SubjectId, localState, remoteState)}",
                LocalSummary = $"{loc.Title} -> {loc.Url}",
                CloudSummary = $"{rem.Title} -> {rem.Url}",
                LocalText = $"{loc.Title}\n{loc.Url}",
                CloudText = $"{rem.Title}\n{rem.Url}",
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = false
            };
            return false;
        }

        private static LinkItem ApplyLinkConflict(LinkItem loc, LinkItem rem, SyncConflictItem resolved)
        {
            string now = DateTime.UtcNow.ToString("O");
            var pick = resolved.Choice == SyncConflictChoice.KeepCloud ? rem : loc;
            return new LinkItem { Id = pick.Id, SubjectId = pick.SubjectId, Title = pick.Title, Url = pick.Url, UpdatedAtUtc = now };
        }

        private static bool MergeCalendarEvent(CalendarEventItem loc, CalendarEventItem rem, CalendarEventItem? bas, AppStateData localState, AppStateData remoteState, out CalendarEventItem merged, out SyncConflictItem? conflict)
        {
            conflict = null;
            bool onlyCompletionOrFlagsDiff =
                string.Equals(loc.Title, rem.Title, StringComparison.Ordinal) &&
                string.Equals(loc.Note, rem.Note, StringComparison.Ordinal) &&
                string.Equals(loc.Date, rem.Date, StringComparison.Ordinal) &&
                string.Equals(loc.EventTime, rem.EventTime, StringComparison.Ordinal);

            if (onlyCompletionOrFlagsDiff)
            {
                merged = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
                return true;
            }

            merged = loc;
            conflict = new SyncConflictItem
            {
                ConflictId = $"calendar_event:{loc.Id}",
                EntityType = SyncConflictType.CalendarEvent,
                EntityId = loc.Id,
                Title = $"Calendar Event: {loc.Title}",
                LocalSummary = $"{loc.Title} ({loc.Date} {loc.EventTime})",
                CloudSummary = $"{rem.Title} ({rem.Date} {rem.EventTime})",
                LocalText = $"Title: {loc.Title}\nWhen: {loc.Date} {loc.EventTime}\nNote: {loc.Note}",
                CloudText = $"Title: {rem.Title}\nWhen: {rem.Date} {rem.EventTime}\nNote: {rem.Note}",
                LocalUpdatedAtUtc = loc.UpdatedAtUtc,
                CloudUpdatedAtUtc = rem.UpdatedAtUtc,
                SupportsCombine = true
            };
            return false;
        }

        private static CalendarEventItem ApplyCalendarEventConflict(CalendarEventItem loc, CalendarEventItem rem, SyncConflictItem resolved)
        {
            string now = DateTime.UtcNow.ToString("O");
            bool remNewer = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc);
            return resolved.Choice switch
            {
                SyncConflictChoice.KeepCloud => CloneCal(rem, now),
                SyncConflictChoice.CombineBoth => new CalendarEventItem
                {
                    Id = loc.Id,
                    Title = string.Equals(loc.Title, rem.Title, StringComparison.Ordinal) ? loc.Title : $"{loc.Title} / {rem.Title}",
                    Note = CombineTwoTexts(loc.Note, rem.Note),
                    Date = remNewer ? rem.Date : loc.Date,
                    EventTime = remNewer ? rem.EventTime : loc.EventTime,
                    SubjectId = string.IsNullOrWhiteSpace(loc.SubjectId) ? rem.SubjectId : loc.SubjectId,
                    Notify3DaysBefore = loc.Notify3DaysBefore || rem.Notify3DaysBefore,
                    Notify24HoursBefore = loc.Notify24HoursBefore || rem.Notify24HoursBefore,
                    NotifyMorningOf = loc.NotifyMorningOf || rem.NotifyMorningOf,
                    NotifyCustom = loc.NotifyCustom || rem.NotifyCustom,
                    CustomOffsetMinutes = remNewer ? rem.CustomOffsetMinutes : loc.CustomOffsetMinutes,
                    CustomTriggerIso = remNewer ? rem.CustomTriggerIso : loc.CustomTriggerIso,
                    Completed = loc.Completed || rem.Completed,
                    CreatedAt = loc.CreatedAt,
                    UpdatedAtUtc = now
                },
                _ => CloneCal(loc, now)
            };
        }

        private static CalendarEventItem CloneCal(CalendarEventItem src, string now) => new()
        {
            Id = src.Id, Title = src.Title, Note = src.Note, Date = src.Date, EventTime = src.EventTime,
            SubjectId = src.SubjectId, Notify3DaysBefore = src.Notify3DaysBefore, Notify24HoursBefore = src.Notify24HoursBefore,
            NotifyMorningOf = src.NotifyMorningOf, NotifyCustom = src.NotifyCustom, CustomOffsetMinutes = src.CustomOffsetMinutes,
            CustomTriggerIso = src.CustomTriggerIso, Completed = src.Completed, CreatedAt = src.CreatedAt, UpdatedAtUtc = now
        };

        private static StudyFileItem MergeStudyFileMetadata(StudyFileItem loc, StudyFileItem rem)
        {
            var newer = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
            var older = ReferenceEquals(newer, rem) ? loc : rem;
            return new StudyFileItem
            {
                Id = newer.Id,
                SubjectId = string.IsNullOrWhiteSpace(newer.SubjectId) ? older.SubjectId : newer.SubjectId,
                OriginalName = string.IsNullOrWhiteSpace(newer.OriginalName) ? older.OriginalName : newer.OriginalName,
                Filename = string.IsNullOrWhiteSpace(newer.Filename) ? older.Filename : newer.Filename,
                Folder = string.IsNullOrWhiteSpace(newer.Folder) ? older.Folder : newer.Folder,
                Sha256 = string.IsNullOrWhiteSpace(newer.Sha256) ? older.Sha256 : newer.Sha256,
                FileSizeBytes = newer.FileSizeBytes > 0 ? newer.FileSizeBytes : older.FileSizeBytes,
                UpdatedAtUtc = MaxUtc(loc.UpdatedAtUtc, rem.UpdatedAtUtc)
            };
        }

        private static MediaTrackItem MergeMediaMetadata(MediaTrackItem loc, MediaTrackItem rem)
        {
            var newer = ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
            var older = ReferenceEquals(newer, rem) ? loc : rem;
            return new MediaTrackItem
            {
                Id = newer.Id,
                SubjectId = string.IsNullOrWhiteSpace(newer.SubjectId) ? older.SubjectId : newer.SubjectId,
                Title = string.IsNullOrWhiteSpace(newer.Title) ? older.Title : newer.Title,
                Filename = string.IsNullOrWhiteSpace(newer.Filename) ? older.Filename : newer.Filename,
                Date = string.IsNullOrWhiteSpace(newer.Date) ? older.Date : newer.Date,
                Duration = string.IsNullOrWhiteSpace(newer.Duration) ? older.Duration : newer.Duration,
                Sha256 = string.IsNullOrWhiteSpace(newer.Sha256) ? older.Sha256 : newer.Sha256,
                FileSizeBytes = newer.FileSizeBytes > 0 ? newer.FileSizeBytes : older.FileSizeBytes,
                UpdatedAtUtc = MaxUtc(loc.UpdatedAtUtc, rem.UpdatedAtUtc)
            };
        }

        private static WhiteboardItem MergeWhiteboardLatestOrUnion(WhiteboardItem loc, WhiteboardItem rem)
        {
            return ParseUtcOrMin(rem.UpdatedAtUtc) > ParseUtcOrMin(loc.UpdatedAtUtc) ? rem : loc;
        }

        private static UserSettings MergeSettings(AppStateData local, AppStateData remote, AppStateData? baseState)
        {
            var ls = local.Settings ?? new UserSettings();
            var rs = remote.Settings ?? new UserSettings();
            var bs = baseState?.Settings;

            bool takeRemotePortable = false;
            if (!HasRealUserContent(local) && HasRealUserContent(remote))
            {
                takeRemotePortable = true;
            }
            else if (bs != null)
            {
                string locHash = HashPortableSettings(ls);
                string remHash = HashPortableSettings(rs);
                string baseHash = HashPortableSettings(bs);
                if (locHash == baseHash && remHash != baseHash) takeRemotePortable = true;
                else if (locHash != baseHash && remHash != baseHash)
                    takeRemotePortable = ParseUtcOrMin(rs.SettingsUpdatedAtUtc) > ParseUtcOrMin(ls.SettingsUpdatedAtUtc);
            }
            else
            {
                takeRemotePortable = ParseUtcOrMin(rs.SettingsUpdatedAtUtc) > ParseUtcOrMin(ls.SettingsUpdatedAtUtc);
            }

            var portableSrc = takeRemotePortable ? rs : ls;
            var mergedSettings = new UserSettings
            {
                // Strictly device-local properties
                DefaultSaveLocation = ls.DefaultSaveLocation,
                ZenVideoFolder = ls.ZenVideoFolder,
                Startup = ls.Startup,
                LaunchWindowMode = ls.LaunchWindowMode,
                InstalledCommitSha = ls.InstalledCommitSha,
                LastUpdateCheck = ls.LastUpdateCheck,
                LastSyncStatusText = ls.LastSyncStatusText,

                // Portable cross-device settings
                ZenScene = portableSrc.ZenScene,
                BgTheme = portableSrc.BgTheme,
                ParticleEffect = portableSrc.ParticleEffect,
                ClockStyle = portableSrc.ClockStyle,
                TimeFormat = portableSrc.TimeFormat,
                FontScale = portableSrc.FontScale,
                Notifications = portableSrc.Notifications,
                CalendarNotifsEnabled = portableSrc.CalendarNotifsEnabled,
                CalendarNotifPresets = (portableSrc.CalendarNotifPresets ?? new()).ToList(),
                CalendarNotifStartHour = portableSrc.CalendarNotifStartHour,
                CalendarNotifCustomHours = portableSrc.CalendarNotifCustomHours,
                PdfOpenMode = portableSrc.PdfOpenMode,
                PdfDarkMode = portableSrc.PdfDarkMode,
                PerfThrottle = portableSrc.PerfThrottle,
                PomoWork = portableSrc.PomoWork,
                PomoBreak = portableSrc.PomoBreak,
                MixerMaster = portableSrc.MixerMaster,
                MixerMuted = portableSrc.MixerMuted,
                MixerLevels = new Dictionary<string, int>(portableSrc.MixerLevels ?? new()),
                GithubRepoUrl = portableSrc.GithubRepoUrl,
                AutoCheckUpdates = portableSrc.AutoCheckUpdates,
                AutoSyncEnabled = ls.AutoSyncEnabled,
                SyncFilesEnabled = ls.SyncFilesEnabled,
                WifiOnlyFileSync = ls.WifiOnlyFileSync,
                SettingsUpdatedAtUtc = MaxUtc(ls.SettingsUpdatedAtUtc, rs.SettingsUpdatedAtUtc)
            };
            mergedSettings.Normalize();
            return mergedSettings;
        }

        private static void PreserveDeviceLocalSettings(AppStateData local, AppStateData target)
        {
            var ls = local.Settings ?? new UserSettings();
            target.Settings ??= new UserSettings();
            target.Settings.DefaultSaveLocation = ls.DefaultSaveLocation;
            target.Settings.ZenVideoFolder = ls.ZenVideoFolder;
            target.Settings.Startup = ls.Startup;
            target.Settings.LaunchWindowMode = ls.LaunchWindowMode;
            target.Settings.InstalledCommitSha = ls.InstalledCommitSha;
            target.Settings.LastUpdateCheck = ls.LastUpdateCheck;
            target.Settings.LastSyncStatusText = ls.LastSyncStatusText;
            target.Settings.AutoSyncEnabled = ls.AutoSyncEnabled;
            target.Settings.SyncFilesEnabled = ls.SyncFilesEnabled;
            target.Settings.WifiOnlyFileSync = ls.WifiOnlyFileSync;
            target.CurrentSubjectId = !string.IsNullOrWhiteSpace(local.CurrentSubjectId) && target.Subjects.Any(s => s.Id == local.CurrentSubjectId && !s.Archived)
                ? local.CurrentSubjectId
                : null;
            target.Normalize();
        }

        private static void ComputeFileTransfers(
            SyncMergeOutcome outcome,
            IReadOnlyDictionary<string, LocalBinaryFileMeta>? localFiles,
            IReadOnlyDictionary<string, RemoteBinaryFileMeta>? remoteFiles,
            IReadOnlyDictionary<string, LocalBinaryFileMeta>? localSounds,
            IReadOnlyDictionary<string, RemoteBinaryFileMeta>? remoteSounds,
            bool uploadsAllowed,
            bool downloadsAllowed)
        {
            var lf = localFiles ?? new Dictionary<string, LocalBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);
            var rf = remoteFiles ?? new Dictionary<string, RemoteBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);
            var ls = localSounds ?? new Dictionary<string, LocalBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);
            var rs = remoteSounds ?? new Dictionary<string, RemoteBinaryFileMeta>(StringComparer.OrdinalIgnoreCase);

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ProcessFileOp(string category, string id, string subjectId, string fileName, string title, string sha, long size)
            {
                if (string.IsNullOrWhiteSpace(fileName) || !seenFiles.Add(fileName)) return;
                bool hasLocal = lf.TryGetValue(fileName, out var locMeta);
                bool hasRemote = rf.TryGetValue(fileName, out var remMeta);

                if (uploadsAllowed && hasLocal && locMeta != null)
                {
                    bool remoteNeeds = !hasRemote || (remMeta != null && locMeta.SizeBytes > 0 && remMeta.SizeBytes != locMeta.SizeBytes);
                    if (remoteNeeds)
                    {
                        outcome.FilesToUpload.Add(new SyncFileTransferOp
                        {
                            Category = category, EntityId = id, SubjectId = subjectId,
                            FileName = fileName, DisplayTitle = title,
                            Sha256 = string.IsNullOrWhiteSpace(locMeta.Sha256) ? sha : locMeta.Sha256,
                            SizeBytes = locMeta.SizeBytes, IsUpload = true
                        });
                    }
                }

                if (downloadsAllowed && !hasLocal && hasRemote && remMeta != null)
                {
                    outcome.FilesToDownload.Add(new SyncFileTransferOp
                    {
                        Category = category, EntityId = id, SubjectId = subjectId,
                        FileName = fileName, DisplayTitle = title,
                        Sha256 = sha, SizeBytes = remMeta.SizeBytes > 0 ? remMeta.SizeBytes : size, IsUpload = false
                    });
                }
            }

            foreach (var p in outcome.MergedState.Pdfs) ProcessFileOp("pdf", p.Id, p.SubjectId, p.Filename, p.OriginalName, p.Sha256, p.FileSizeBytes);
            foreach (var v in outcome.MergedState.Videos) ProcessFileOp("video", v.Id, v.SubjectId, v.Filename, v.Title, v.Sha256, v.FileSizeBytes);
            foreach (var r in outcome.MergedState.Recordings) ProcessFileOp("recording", r.Id, r.SubjectId, r.Filename, r.Title, r.Sha256, r.FileSizeBytes);

            var seenSounds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in outcome.MergedState.CustomSounds)
            {
                if (string.IsNullOrWhiteSpace(s.Filename) || !seenSounds.Add(s.Filename)) continue;
                bool hasLocal = ls.TryGetValue(s.Filename, out var locMeta);
                bool hasRemote = rs.TryGetValue(s.Filename, out var remMeta);

                if (uploadsAllowed && hasLocal && locMeta != null)
                {
                    bool remoteNeeds = !hasRemote || (remMeta != null && locMeta.SizeBytes > 0 && remMeta.SizeBytes != locMeta.SizeBytes);
                    if (remoteNeeds)
                    {
                        outcome.SoundsToUpload.Add(new SyncFileTransferOp
                        {
                            Category = "sound", EntityId = s.Id, SubjectId = "",
                            FileName = s.Filename, DisplayTitle = s.Name,
                            Sha256 = locMeta.Sha256, SizeBytes = locMeta.SizeBytes, IsUpload = true
                        });
                    }
                }

                if (downloadsAllowed && !hasLocal && hasRemote && remMeta != null)
                {
                    outcome.SoundsToDownload.Add(new SyncFileTransferOp
                    {
                        Category = "sound", EntityId = s.Id, SubjectId = "",
                        FileName = s.Filename, DisplayTitle = s.Name,
                        Sha256 = s.Sha256, SizeBytes = remMeta.SizeBytes > 0 ? remMeta.SizeBytes : s.FileSizeBytes, IsUpload = false
                    });
                }
            }
        }

        private static Dictionary<string, int> MergeIntMaxDict(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            var res = new Dictionary<string, int>(a ?? new(), StringComparer.Ordinal);
            foreach (var kv in b ?? new())
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                res[kv.Key] = res.TryGetValue(kv.Key, out var cur) ? Math.Max(cur, kv.Value) : kv.Value;
            }
            return res;
        }

        private static Dictionary<string, double> MergeDoubleMaxDict(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            var res = new Dictionary<string, double>(a ?? new(), StringComparer.Ordinal);
            foreach (var kv in b ?? new())
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                res[kv.Key] = res.TryGetValue(kv.Key, out var cur) ? Math.Max(cur, kv.Value) : kv.Value;
            }
            return res;
        }

        private static Dictionary<string, T> ToIdDict<T>(List<T> list, Func<T, string> getId)
        {
            var dict = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in list)
            {
                string id = getId(item);
                if (!string.IsNullOrWhiteSpace(id) && !dict.ContainsKey(id))
                    dict[id] = item;
            }
            return dict;
        }

        private static Dictionary<string, SyncTombstone> BuildTombstoneMap(List<SyncTombstone> list)
        {
            var dict = new Dictionary<string, SyncTombstone>(StringComparer.Ordinal);
            foreach (var t in list ?? new())
            {
                if (string.IsNullOrWhiteSpace(t.EntityType) || string.IsNullOrWhiteSpace(t.EntityId)) continue;
                string key = $"{t.EntityType}:{t.EntityId}";
                if (!dict.TryGetValue(key, out var existing) || ParseUtcOrMin(t.DeletedAtUtc) > ParseUtcOrMin(existing.DeletedAtUtc))
                {
                    dict[key] = t;
                }
            }
            return dict;
        }

        private static List<SyncTombstone> MergeTombstones(List<SyncTombstone> a, List<SyncTombstone> b)
        {
            var map = BuildTombstoneMap(a ?? new());
            foreach (var t in b ?? new())
            {
                if (string.IsNullOrWhiteSpace(t.EntityType) || string.IsNullOrWhiteSpace(t.EntityId)) continue;
                string key = $"{t.EntityType}:{t.EntityId}";
                if (!map.TryGetValue(key, out var existing) || ParseUtcOrMin(t.DeletedAtUtc) > ParseUtcOrMin(existing.DeletedAtUtc))
                    map[key] = t;
            }
            return map.Values.ToList();
        }

        private static void UpsertTombstone(List<SyncTombstone> list, SyncTombstone tomb)
        {
            int idx = list.FindIndex(t => t.EntityType == tomb.EntityType && t.EntityId == tomb.EntityId);
            if (idx >= 0) list[idx] = tomb;
            else list.Add(tomb);
        }

        private static void RemoveMatchingTombstone(List<SyncTombstone> list, string compositeKey)
        {
            int sep = compositeKey.IndexOf(':');
            if (sep <= 0) return;
            string eType = compositeKey[..sep];
            string eId = compositeKey[(sep + 1)..];
            list.RemoveAll(t => t.EntityType == eType && t.EntityId == eId);
        }

        private static void PruneTombstones(List<SyncTombstone> list)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-120);
            list.RemoveAll(t => ParseUtcOrMin(t.DeletedAtUtc) != DateTimeOffset.MinValue && ParseUtcOrMin(t.DeletedAtUtc) < cutoff);
            if (list.Count > 2000)
            {
                var ordered = list
                    .OrderByDescending(t => ParseUtcOrMin(t.DeletedAtUtc))
                    .Take(2000)
                    .ToList();
                list.Clear();
                list.AddRange(ordered);
            }
        }

        private static string FindSubjectName(string? subjectId, AppStateData a, AppStateData b)
        {
            if (string.IsNullOrWhiteSpace(subjectId)) return "General";
            return a.Subjects.FirstOrDefault(s => s.Id == subjectId)?.Name
                ?? b.Subjects.FirstOrDefault(s => s.Id == subjectId)?.Name
                ?? "Course";
        }

        private static string TruncatePreview(string? text, int maxLen)
        {
            string flat = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= maxLen ? flat : flat[..(maxLen - 3)] + "...";
        }

        private static string CombineTwoTexts(string? a, string? b)
        {
            string ta = (a ?? "").Trim();
            string tb = (b ?? "").Trim();
            if (string.IsNullOrEmpty(ta)) return tb;
            if (string.IsNullOrEmpty(tb)) return ta;
            if (string.Equals(ta, tb, StringComparison.Ordinal)) return ta;
            return $"{ta}\n\n--- [Merged] ---\n{tb}";
        }

        private static string HashSubject(SubjectItem s) => ComputeStringSha256(
            $"{s.Name}|{s.Icon}|{s.Color}|{s.Archived}|{string.Join(",", (s.Folders ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");

        private static string HashStudyFile(StudyFileItem f) => ComputeStringSha256(
            $"{f.SubjectId}|{f.OriginalName}|{f.Filename}|{f.Folder}|{f.Sha256}|{f.FileSizeBytes}");

        private static string HashMedia(MediaTrackItem m) => ComputeStringSha256(
            $"{m.SubjectId}|{m.Title}|{m.Filename}|{m.Date}|{m.Duration}|{m.Sha256}|{m.FileSizeBytes}");

        private static string HashNote(NoteItem n) => ComputeStringSha256($"{n.SubjectId}|{n.Text}|{n.Date}");
        private static string HashTask(TaskItem t) => ComputeStringSha256($"{t.SubjectId}|{t.Text}|{t.Completed}");
        private static string HashFlashcard(FlashcardItem f) => ComputeStringSha256($"{f.SubjectId}|{f.Type}|{f.Question}|{f.Answer}");
        private static string HashWhiteboard(WhiteboardItem w) => ComputeStringSha256($"{w.SubjectId}|{w.ImageDataUrl}");
        private static string HashDeadline(DeadlineItem d) => ComputeStringSha256($"{d.SubjectId}|{d.Title}|{d.Type}|{d.Date}|{d.Time}|{d.Completed}|{d.CompletedAt}|{d.Notes}");
        private static string HashLink(LinkItem l) => ComputeStringSha256($"{l.SubjectId}|{l.Title}|{l.Url}");
        private static string HashCalendarEvent(CalendarEventItem c) => ComputeStringSha256(
            $"{c.Title}|{c.Note}|{c.Date}|{c.EventTime}|{c.SubjectId}|{c.Notify3DaysBefore}|{c.Notify24HoursBefore}|{c.NotifyMorningOf}|{c.NotifyCustom}|{c.CustomOffsetMinutes}|{c.CustomTriggerIso}|{c.Completed}");
        private static string HashCustomSound(CustomSoundItem s) => ComputeStringSha256($"{s.Name}|{s.Filename}|{s.OriginalName}|{s.Url}|{s.Sha256}|{s.FileSizeBytes}");
        private static string HashSoundPreset(SoundPresetItem p)
        {
            var levelsStr = string.Join(",", (p.Levels ?? new()).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            return ComputeStringSha256($"{p.Name}|{levelsStr}");
        }

        private static string HashPortableSettings(UserSettings u)
        {
            var presets = string.Join(",", u.CalendarNotifPresets ?? new());
            var mixer = string.Join(",", (u.MixerLevels ?? new()).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            return ComputeStringSha256(
                $"{u.ZenScene}|{u.BgTheme}|{u.ParticleEffect}|{u.ClockStyle}|{u.TimeFormat}|{u.FontScale}|{u.Notifications}|{u.CalendarNotifsEnabled}|{presets}|{u.CalendarNotifStartHour}|{u.CalendarNotifCustomHours}|{u.PdfOpenMode}|{u.PdfDarkMode}|{u.PerfThrottle}|{u.PomoWork}|{u.PomoBreak}|{u.MixerMaster}|{u.MixerMuted}|{mixer}|{u.GithubRepoUrl}|{u.AutoCheckUpdates}");
        }

        private static void RegisterAllEntities(AppStateData s, DynamicRegisterHelper cb)
        {
            cb.Call("subject", s.Subjects, x => x.Id, x => x.Id, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashSubject);
            cb.Call("pdf", s.Pdfs, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashStudyFile);
            cb.Call("video", s.Videos, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashMedia);
            cb.Call("recording", s.Recordings, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashMedia);
            cb.Call("note", s.Notes, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashNote);
            cb.Call("task", s.Tasks, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashTask);
            cb.Call("flashcard", s.Flashcards, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashFlashcard);
            cb.Call("whiteboard", s.Whiteboards, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashWhiteboard);
            cb.Call("deadline", s.Deadlines, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashDeadline);
            cb.Call("link", s.Links, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashLink);
            cb.Call("calendar_event", s.CalendarEvents, x => x.Id, x => x.SubjectId, x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashCalendarEvent);
            cb.Call("custom_sound", s.CustomSounds, x => x.Id, _ => "", x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashCustomSound);
            cb.Call("sound_preset", s.SavedSoundPresets, x => x.Id, _ => "", x => x.UpdatedAtUtc, (x, v) => x.UpdatedAtUtc = v, HashSoundPreset);
        }

        private sealed class DynamicRegisterHelper
        {
            private readonly Dictionary<string, (string Hash, string SubjectId, string UpdatedAt, Action<string> SetUpdatedAt)> _map;

            public DynamicRegisterHelper(Dictionary<string, (string Hash, string SubjectId, string UpdatedAt, Action<string> SetUpdatedAt)> map)
            {
                _map = map;
            }

            public void Call<T>(
                string typeName,
                List<T> list,
                Func<T, string> getId,
                Func<T, string> getSubjectId,
                Func<T, string> getUpdated,
                Action<T, string> setUpdated,
                Func<T, string> getHash)
            {
                foreach (var item in list)
                {
                    string id = getId(item);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    string key = $"{typeName}:{id}";
                    if (!_map.ContainsKey(key))
                    {
                        var captured = item;
                        _map[key] = (
                            getHash(item),
                            getSubjectId(item) ?? "",
                            getUpdated(item) ?? "",
                            ts => setUpdated(captured, ts)
                        );
                    }
                }
            }
        }
    }
}
