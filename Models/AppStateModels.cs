using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyHub.Models
{
    public class FlexibleStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString() ?? "";
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out long l)) return l.ToString();
                if (reader.TryGetDouble(out double d)) return d.ToString();
            }
            if (reader.TokenType == JsonTokenType.True) return "true";
            if (reader.TokenType == JsonTokenType.False) return "false";
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.TryGetProperty("side", out var sideEl))
                {
                    var s = sideEl.GetString();
                    return s == "left" ? "bottom-left" : (s == "top" ? "top-bar" : "bottom-right");
                }
                return "bottom-right";
            }
            if (reader.TokenType == JsonTokenType.Null) return "";
            reader.Skip();
            return "";
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value ?? "");
        }
    }

    public class AppStateData
    {
        [JsonPropertyName("subjects")]
        public List<SubjectItem> Subjects { get; set; } = new();

        [JsonPropertyName("pdfs")]
        public List<StudyFileItem> Pdfs { get; set; } = new();

        [JsonPropertyName("videos")]
        public List<MediaTrackItem> Videos { get; set; } = new();

        [JsonPropertyName("recordings")]
        public List<MediaTrackItem> Recordings { get; set; } = new();

        [JsonPropertyName("notes")]
        public List<NoteItem> Notes { get; set; } = new();

        [JsonPropertyName("tasks")]
        public List<TaskItem> Tasks { get; set; } = new();

        [JsonPropertyName("flashcards")]
        public List<FlashcardItem> Flashcards { get; set; } = new();

        [JsonPropertyName("whiteboards")]
        public List<WhiteboardItem> Whiteboards { get; set; } = new();

        [JsonPropertyName("deadlines")]
        public List<DeadlineItem> Deadlines { get; set; } = new();

        [JsonPropertyName("calendarEvents")]
        public List<CalendarEventItem> CalendarEvents { get; set; } = new();

        [JsonPropertyName("firedNotificationKeys")]
        public List<string> FiredNotificationKeys { get; set; } = new();

        [JsonPropertyName("links")]
        public List<LinkItem> Links { get; set; } = new();

        [JsonPropertyName("customSounds")]
        public List<CustomSoundItem> CustomSounds { get; set; } = new();

        [JsonPropertyName("savedSoundPresets")]
        public List<SoundPresetItem> SavedSoundPresets { get; set; } = new();

        [JsonPropertyName("activity")]
        public Dictionary<string, int> Activity { get; set; } = new();

        [JsonPropertyName("subjectTimes")]
        public Dictionary<string, double> SubjectTimes { get; set; } = new();

        [JsonPropertyName("pdfProgress")]
        public Dictionary<string, int> PdfProgress { get; set; } = new();

        [JsonPropertyName("pomodoroDays")]
        public Dictionary<string, int> PomodoroDays { get; set; } = new();

        [JsonPropertyName("pomodorosCompleted")]
        public int PomodorosCompleted { get; set; } = 0;

        [JsonPropertyName("currentSubjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? CurrentSubjectId { get; set; }

        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "dark";

        [JsonPropertyName("timerDock")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string TimerDock { get; set; } = "bottom-right";

        [JsonPropertyName("settings")]
        public UserSettings Settings { get; set; } = new();

        public void Normalize()
        {
            Subjects ??= new();
            Pdfs ??= new();
            Videos ??= new();
            Recordings ??= new();
            Notes ??= new();
            Tasks ??= new();
            Flashcards ??= new();
            Whiteboards ??= new();
            Deadlines ??= new();
            Links ??= new();
            CustomSounds ??= new();
            SavedSoundPresets ??= new();
            Activity ??= new();
            SubjectTimes ??= new();
            PdfProgress ??= new();
            PomodoroDays ??= new();
            Settings ??= new();
            Settings.Normalize();

            if (string.IsNullOrWhiteSpace(Theme)) Theme = "dark";
            if (TimerDock != "bottom-right" && TimerDock != "bottom-left" && TimerDock != "top-bar")
                TimerDock = "bottom-right";

            foreach (var sub in Subjects)
            {
                if (string.IsNullOrWhiteSpace(sub.Id)) sub.Id = "sub_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (string.IsNullOrWhiteSpace(sub.Name)) sub.Name = "Untitled Subject";
                if (string.IsNullOrWhiteSpace(sub.Icon)) sub.Icon = "fa-graduation-cap";
                if (string.IsNullOrWhiteSpace(sub.Color)) sub.Color = "from-violet-600 via-purple-500 to-fuchsia-500";
                if (sub.Folders == null || sub.Folders.Count == 0)
                {
                    sub.Folders = new List<string> { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" };
                }
            }

            foreach (var d in Deadlines)
            {
                if (string.IsNullOrWhiteSpace(d.Time)) d.Time = "23:59";
                if (string.IsNullOrWhiteSpace(d.Type)) d.Type = "Assignment";
                d.Notes ??= "";
            }
        }
    }

    public class SubjectItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "fa-graduation-cap";

        [JsonPropertyName("color")]
        public string Color { get; set; } = "from-violet-600 via-purple-500 to-fuchsia-500";

        [JsonPropertyName("archived")]
        public bool Archived { get; set; } = false;

        [JsonPropertyName("folders")]
        public List<string> Folders { get; set; } = new() { "Lectures", "Tutorials", "Assignments", "Quizzes", "Labs", "References" };
    }

    public class StudyFileItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("original_name")]
        public string OriginalName { get; set; } = "";

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = "";

        [JsonPropertyName("folder")]
        public string Folder { get; set; } = "Lectures";
    }

    public class MediaTrackItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = "";

        [JsonPropertyName("date")]
        public string Date { get; set; } = "";

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = "";
    }

    public class NoteItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("date")]
        public string Date { get; set; } = "";
    }

    public class TaskItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("completed")]
        public bool Completed { get; set; } = false;
    }

    public class FlashcardItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Definition";

        [JsonPropertyName("q")]
        public string Question { get; set; } = "";

        [JsonPropertyName("a")]
        public string Answer { get; set; } = "";

        [JsonIgnore]
        public bool IsFlipped { get; set; } = false;
    }

    public class WhiteboardItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("img")]
        public string ImageDataUrl { get; set; } = "";
    }

    public class DeadlineItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Assignment";

        [JsonPropertyName("date")]
        public string Date { get; set; } = "";

        [JsonPropertyName("time")]
        public string Time { get; set; } = "23:59";

        [JsonPropertyName("completed")]
        public bool Completed { get; set; } = false;

        [JsonPropertyName("completedAt")]
        public long? CompletedAt { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = "";
    }

    public class LinkItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("subjectId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    public class CustomSoundItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = "";

        [JsonPropertyName("original_name")]
        public string OriginalName { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("addedAt")]
        public string AddedAt { get; set; } = "";
    }

    public class SoundPresetItem
    {
        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("levels")]
        public Dictionary<string, int> Levels { get; set; } = new();
    }

    public class UserSettings
    {
        [JsonPropertyName("defaultSaveLocation")]
        public string DefaultSaveLocation { get; set; } = "";

        [JsonPropertyName("zenVideoFolder")]
        public string ZenVideoFolder { get; set; } = "";

        [JsonPropertyName("zenScene")]
        public string ZenScene { get; set; } = "north_pole";

        [JsonPropertyName("bgTheme")]
        public string BgTheme { get; set; } = "aurora";

        [JsonPropertyName("particleEffect")]
        public string ParticleEffect { get; set; } = "fading_stars";

        [JsonPropertyName("clockStyle")]
        public string ClockStyle { get; set; } = "digital";

        [JsonPropertyName("timeFormat")]
        public string TimeFormat { get; set; } = "12h";

        [JsonPropertyName("launchWindowMode")]
        public string LaunchWindowMode { get; set; } = "windowed";

        [JsonPropertyName("fontScale")]
        public string FontScale { get; set; } = "medium";

        [JsonPropertyName("notifications")]
        public bool Notifications { get; set; } = true;

        public const string DefaultGithubRepoUrl = "https://github.com/InfamousToken10729/Study-Hub-Remastered/tree/study_hub";

        [JsonPropertyName("calendarNotifsEnabled")]
        public bool CalendarNotifsEnabled { get; set; } = true;

        [JsonPropertyName("calendarNotifPresets")]
        public List<string> CalendarNotifPresets { get; set; } = new() { "start_of_day", "1_day", "3_days" };

        [JsonPropertyName("calendarNotifStartHour")]
        public string CalendarNotifStartHour { get; set; } = "08:00";

        [JsonPropertyName("calendarNotifCustomHours")]
        public int CalendarNotifCustomHours { get; set; } = 2;

        [JsonPropertyName("pdfOpenMode")]
        public string PdfOpenMode { get; set; } = "in_app";

        [JsonPropertyName("pdfDarkMode")]
        public bool PdfDarkMode { get; set; } = false;

        [JsonPropertyName("startup")]
        public bool Startup { get; set; } = false;

        [JsonPropertyName("perfThrottle")]
        public bool PerfThrottle { get; set; } = false;

        [JsonPropertyName("pomoWork")]
        public int PomoWork { get; set; } = 25;

        [JsonPropertyName("pomoBreak")]
        public int PomoBreak { get; set; } = 5;

        [JsonPropertyName("mixerMaster")]
        public int MixerMaster { get; set; } = 80;

        [JsonPropertyName("mixerMuted")]
        public bool MixerMuted { get; set; } = false;

        [JsonPropertyName("mixerLevels")]
        public Dictionary<string, int> MixerLevels { get; set; } = new();

        // --- GitHub Auto-Updater Settings ---
        [JsonPropertyName("githubRepoUrl")]
        public string GithubRepoUrl { get; set; } = DefaultGithubRepoUrl;

        public static string NormalizeGithubRepoUrl(string? url)
        {
            var trimmed = (url ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                string.Equals(trimmed, "https://github.com/Mahmoud/StudyHub", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "https://github.com/Mahmoud/StudyHub.git", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultGithubRepoUrl;
            }
            return trimmed;
        }

        [JsonPropertyName("autoCheckUpdates")]
        public bool AutoCheckUpdates { get; set; } = true;

        [JsonPropertyName("installedCommitSha")]
        public string InstalledCommitSha { get; set; } = "";

        [JsonPropertyName("lastUpdateCheck")]
        public string LastUpdateCheck { get; set; } = "";

        public void Normalize()
        {
            DefaultSaveLocation ??= "";
            ZenVideoFolder ??= "";
            if (string.IsNullOrWhiteSpace(ZenScene)) ZenScene = "north_pole";
            if (string.IsNullOrWhiteSpace(BgTheme)) BgTheme = "aurora";
            if (string.IsNullOrWhiteSpace(ParticleEffect)) ParticleEffect = "fading_stars";
            if (string.IsNullOrWhiteSpace(ClockStyle)) ClockStyle = "digital";
            if (string.IsNullOrWhiteSpace(TimeFormat)) TimeFormat = "12h";
            if (string.IsNullOrWhiteSpace(LaunchWindowMode)) LaunchWindowMode = "windowed";
            if (string.IsNullOrWhiteSpace(FontScale)) FontScale = "medium";
            if (string.IsNullOrWhiteSpace(PdfOpenMode)) PdfOpenMode = "in_app";
            if (string.IsNullOrWhiteSpace(CalendarNotifStartHour)) CalendarNotifStartHour = "09:00";
            if (CalendarNotifCustomHours < 1) CalendarNotifCustomHours = 2;
            if (PomoWork < 1) PomoWork = 25;
            if (PomoBreak < 1) PomoBreak = 5;
            CalendarNotifPresets ??= new() { "start_of_day", "1_day", "3_days" };
            MixerLevels ??= new();
            GithubRepoUrl ??= "";
            InstalledCommitSha ??= "";
            LastUpdateCheck ??= "";
        }
    }
}
