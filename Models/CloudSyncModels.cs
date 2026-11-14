using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StudyHub.Models
{
    public enum SyncResolutionMode
    {
        SmartMerge = 0,
        SmartMergeKeepDeleted = 1,
        ForcePullCloud = 2,
        ForcePushLocal = 3
    }

    public enum SyncConflictType
    {
        NoteContent,
        SubjectDetails,
        CalendarEvent,
        Deadline,
        Link,
        Flashcard,
        TaskItem
    }

    public enum SyncConflictChoice
    {
        Pending = 0,
        CombineBoth = 1,
        KeepLocal = 2,
        KeepCloud = 3
    }

    public enum SyncStatusLevel
    {
        NotConfigured = 0,
        SignedOut = 1,
        Idle = 2,
        Syncing = 3,
        Synced = 4,
        NeedsAttention = 5,
        Conflict = 6,
        Error = 7,
        Offline = 8
    }

    public class SyncTombstone
    {
        [JsonPropertyName("entityType")]
        public string EntityType { get; set; } = "";

        [JsonPropertyName("entityId")]
        public string EntityId { get; set; } = "";

        [JsonPropertyName("subjectId")]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("deletedAtUtc")]
        public string DeletedAtUtc { get; set; } = "";

        [JsonPropertyName("deletedByDeviceId")]
        public string DeletedByDeviceId { get; set; } = "";
    }

    public class AppSyncMetadata
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("lastMergedAtUtc")]
        public string LastMergedAtUtc { get; set; } = "";

        [JsonPropertyName("lastMergedByDeviceId")]
        public string LastMergedByDeviceId { get; set; } = "";

        [JsonPropertyName("lastSyncSummary")]
        public string LastSyncSummary { get; set; } = "";

        [JsonPropertyName("tombstones")]
        public List<SyncTombstone> Tombstones { get; set; } = new();

        public void Normalize()
        {
            if (SchemaVersion <= 0) SchemaVersion = 1;
            LastMergedAtUtc ??= "";
            LastMergedByDeviceId ??= "";
            LastSyncSummary ??= "";
            Tombstones ??= new();
        }
    }

    public class SyncConflictItem
    {
        public string ConflictId { get; set; } = "";
        public SyncConflictType EntityType { get; set; }
        public string EntityId { get; set; } = "";
        public string Title { get; set; } = "";
        public string LocalSummary { get; set; } = "";
        public string CloudSummary { get; set; } = "";
        public string LocalText { get; set; } = "";
        public string CloudText { get; set; } = "";
        public string LocalUpdatedAtUtc { get; set; } = "";
        public string CloudUpdatedAtUtc { get; set; } = "";
        public bool SupportsCombine { get; set; } = true;
        public SyncConflictChoice Choice { get; set; } = SyncConflictChoice.Pending;
    }

    public class SyncDatasetCounts
    {
        public int Subjects { get; set; }
        public int Pdfs { get; set; }
        public int Videos { get; set; }
        public int Recordings { get; set; }
        public int Notes { get; set; }
        public int Tasks { get; set; }
        public int Flashcards { get; set; }
        public int Whiteboards { get; set; }
        public int Deadlines { get; set; }
        public int Links { get; set; }
        public int CalendarEvents { get; set; }
        public int CustomSounds { get; set; }
        public int SoundPresets { get; set; }
        public int TotalItems => Subjects + Pdfs + Videos + Recordings + Notes + Tasks + Flashcards + Whiteboards + Deadlines + Links + CalendarEvents + CustomSounds + SoundPresets;
    }

    public class SyncMismatchReport
    {
        public bool RequiresConfirmation { get; set; }
        public bool IsFirstSync { get; set; }
        public bool IsLargeDeletionWarning { get; set; }
        public string ReasonTitle { get; set; } = "";
        public string ReasonDescription { get; set; } = "";
        public SyncDatasetCounts LocalCounts { get; set; } = new();
        public SyncDatasetCounts CloudCounts { get; set; } = new();
        public int WouldRemoveSubjectsCount { get; set; }
        public int WouldRemoveItemsCount { get; set; }
        public List<string> WouldRemoveSubjectNames { get; set; } = new();
    }

    public class SyncFileTransferOp
    {
        public string Category { get; set; } = ""; // "pdf" | "video" | "recording" | "sound"
        public string EntityId { get; set; } = "";
        public string SubjectId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DisplayTitle { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long SizeBytes { get; set; }
        public bool IsUpload { get; set; }
    }

    public class SyncMergeOutcome
    {
        public AppStateData MergedState { get; set; } = new();
        public SyncMismatchReport MismatchReport { get; set; } = new();
        public List<SyncConflictItem> Conflicts { get; set; } = new();
        public List<SyncFileTransferOp> FilesToUpload { get; set; } = new();
        public List<SyncFileTransferOp> FilesToDownload { get; set; } = new();
        public List<SyncFileTransferOp> SoundsToUpload { get; set; } = new();
        public List<SyncFileTransferOp> SoundsToDownload { get; set; } = new();
        public int LocalChangesCount { get; set; }
        public int CloudChangesAppliedCount { get; set; }
        public string StatusSummary { get; set; } = "";
        public bool RequiresUserAction => MismatchReport.RequiresConfirmation || Conflicts.Count > 0;
    }

    public class CloudSnapshotInfo
    {
        public string DriveFileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public long SizeBytes { get; set; }
        public string SummaryText { get; set; } = "";
    }

    public class GoogleDriveConfig
    {
        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = "";

        [JsonPropertyName("clientSecret")]
        public string ClientSecret { get; set; } = "";

        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = "";

        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [JsonPropertyName("userEmail")]
        public string UserEmail { get; set; } = "";

        [JsonPropertyName("userName")]
        public string UserName { get; set; } = "";

        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = "";

        [JsonPropertyName("tokenExpiresAtUtc")]
        public string TokenExpiresAtUtc { get; set; } = "";

        [JsonPropertyName("driveRootFolderId")]
        public string DriveRootFolderId { get; set; } = "";

        [JsonPropertyName("driveFilesFolderId")]
        public string DriveFilesFolderId { get; set; } = "";

        [JsonPropertyName("driveSoundsFolderId")]
        public string DriveSoundsFolderId { get; set; } = "";

        [JsonPropertyName("driveSnapshotsFolderId")]
        public string DriveSnapshotsFolderId { get; set; } = "";

        [JsonPropertyName("remoteStateFileId")]
        public string RemoteStateFileId { get; set; } = "";

        [JsonPropertyName("lastSyncedRemoteMd5")]
        public string LastSyncedRemoteMd5 { get; set; } = "";

        [JsonPropertyName("lastSyncedRemoteVersion")]
        public long LastSyncedRemoteVersion { get; set; } = 0;

        [JsonPropertyName("lastSyncedAtUtc")]
        public string LastSyncedAtUtc { get; set; } = "";

        [JsonPropertyName("lastSyncResultMessage")]
        public string LastSyncResultMessage { get; set; } = "";

        [JsonPropertyName("isSignedIn")]
        public bool IsSignedIn { get; set; } = false;

        public void Normalize()
        {
            ClientId ??= "";
            ClientSecret ??= "";
            if (string.IsNullOrWhiteSpace(DeviceId))
                DeviceId = $"dev_{Guid.NewGuid().ToString("N")[..12]}";
            if (string.IsNullOrWhiteSpace(DeviceName))
                DeviceName = $"{Environment.MachineName} ({OperatingSystem.IsAndroid() switch { true => "Android", _ => "Windows" }})";
            UserEmail ??= "";
            UserName ??= "";
            AccessToken ??= "";
            RefreshToken ??= "";
            TokenExpiresAtUtc ??= "";
            DriveRootFolderId ??= "";
            DriveFilesFolderId ??= "";
            DriveSoundsFolderId ??= "";
            DriveSnapshotsFolderId ??= "";
            RemoteStateFileId ??= "";
            LastSyncedRemoteMd5 ??= "";
            LastSyncedAtUtc ??= "";
            LastSyncResultMessage ??= "";
        }

        [JsonIgnore]
        public bool HasClientCredentials => !string.IsNullOrWhiteSpace(ClientId);

        [JsonIgnore]
        public bool HasValidSession => IsSignedIn && !string.IsNullOrWhiteSpace(RefreshToken);
    }

    public class SyncEntityEntry
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("subjectId")]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("updatedAtUtc")]
        public string UpdatedAtUtc { get; set; } = "";
    }

    public class SyncEntityHashManifest
    {
        [JsonPropertyName("initialized")]
        public bool Initialized { get; set; }

        [JsonPropertyName("entities")]
        public Dictionary<string, SyncEntityEntry> Entities { get; set; } = new();

        public void Normalize()
        {
            Entities ??= new();
        }
    }

    public class SyncProgressReport
    {
        public SyncStatusLevel Level { get; set; } = SyncStatusLevel.Idle;
        public string Headline { get; set; } = "Ready to Sync";
        public string Detail { get; set; } = "";
        public string SyncedAtUtc { get; set; } = "";
        public int PendingConflictCount { get; set; }
    }

    public class GoogleQuickPairPayload
    {
        [JsonPropertyName("studyHubPairVersion")]
        public int StudyHubPairVersion { get; set; } = 1;

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = "";

        [JsonPropertyName("clientSecret")]
        public string ClientSecret { get; set; } = "";

        [JsonPropertyName("createdAtUtc")]
        public string CreatedAtUtc { get; set; } = "";
    }
}
