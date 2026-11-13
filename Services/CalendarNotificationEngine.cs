using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StudyHub.Models;

namespace StudyHub.Services
{
    public class ScheduledTriggerInfo
    {
        public string FiredKey { get; set; } = "";
        public string RuleKey { get; set; } = ""; // "3_days", "24_hours", "morning", "custom"
        public string RuleLabel { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Note { get; set; } = "";
        public string SubjectName { get; set; } = "";
        public DateTime EventDateTime { get; set; }
        public DateTime TriggerDateTime { get; set; }
        public int NotificationId { get; set; }
    }

    public class UpcomingEventEntry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Note { get; set; } = "";
        public DateTime DateTimeLocal { get; set; }
        public string DateKey { get; set; } = "";
        public string TimeDisplay { get; set; } = "";
        public string RelativeBadge { get; set; } = "";
        public string CountdownColorHex { get; set; } = "#818CF8";
        public string SubjectName { get; set; } = "";
        public string SubjectIcon { get; set; } = "📅";
        public string ReminderSummary { get; set; } = "";
        public bool IsDeadlineSource { get; set; }
        public bool Completed { get; set; }
        public bool IsOverdue { get; set; }
        public CalendarEventItem? SourceCalendarEvent { get; set; }
        public DeadlineItem? SourceDeadline { get; set; }
    }

    public static class CalendarNotificationEngine
    {
        public static TimeSpan ParseTimeOfDay(string? hhmm, TimeSpan fallback)
        {
            if (!string.IsNullOrWhiteSpace(hhmm))
            {
                string trimmed = hhmm.Trim();
                if (TimeSpan.TryParseExact(trimmed, new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var ts))
                    return new TimeSpan(Math.Clamp(ts.Hours, 0, 23), Math.Clamp(ts.Minutes, 0, 59), 0);
                if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var dt))
                    return dt.TimeOfDay;
            }
            return fallback;
        }

        public static string NormalizeMorningTimeString(string? raw)
        {
            var ts = ParseTimeOfDay(raw, new TimeSpan(8, 0, 0));
            return $"{ts.Hours:D2}:{ts.Minutes:D2}";
        }

        public static DateTime ParseEventDateTime(string? dateStr, string? timeStr, TimeSpan defaultTime)
        {
            DateTime d = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(dateStr))
            {
                if (!DateTime.TryParseExact(dateStr.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                {
                    DateTime.TryParse(dateStr.Trim(), out d);
                }
            }
            var t = ParseTimeOfDay(timeStr, defaultTime);
            return d.Date + t;
        }

        public static string FormatReminderSummary(CalendarEventItem ev, UserSettings? settings)
        {
            var parts = new List<string>();
            if (ev.Notify3DaysBefore) parts.Add("3d");
            if (ev.Notify24HoursBefore) parts.Add("24h");
            if (ev.NotifyMorningOf)
            {
                string m = NormalizeMorningTimeString(settings?.CalendarNotifStartHour);
                parts.Add($"Morning {m}");
            }
            if (ev.NotifyCustom)
            {
                if (!string.IsNullOrWhiteSpace(ev.CustomTriggerIso))
                    parts.Add("Custom time");
                else
                    parts.Add(FormatCustomOffsetShort(ev.CustomOffsetMinutes));
            }
            return parts.Count > 0 ? string.Join(" · ", parts) : "No alerts";
        }

        public static string FormatCustomOffsetShort(int minutes)
        {
            int m = Math.Max(1, minutes);
            if (m % 1440 == 0) return $"-{m / 1440}d";
            if (m % 60 == 0) return $"-{m / 60}h";
            return $"-{m}m";
        }

        public static List<ScheduledTriggerInfo> BuildTriggersForEvent(CalendarEventItem ev, AppStateData state)
        {
            var list = new List<ScheduledTriggerInfo>();
            if (ev == null || ev.Completed) return list;

            var settings = state.Settings ?? new UserSettings();
            if (!settings.CalendarNotifsEnabled) return list;

            var eventDt = ParseEventDateTime(ev.Date, ev.EventTime, new TimeSpan(9, 0, 0));
            var morningTod = ParseTimeOfDay(settings.CalendarNotifStartHour, new TimeSpan(8, 0, 0));
            string subjName = string.IsNullOrWhiteSpace(ev.SubjectId)
                ? ""
                : (state.Subjects.FirstOrDefault(s => s.Id == ev.SubjectId)?.Name ?? "");

            void AddRule(string ruleKey, string ruleLabel, DateTime triggerDt)
            {
                string key = $"cal_{ev.Id}_{ruleKey}_{triggerDt:yyyyMMddHHmm}";
                list.Add(new ScheduledTriggerInfo
                {
                    FiredKey = key,
                    RuleKey = ruleKey,
                    RuleLabel = ruleLabel,
                    EventId = ev.Id,
                    Title = string.IsNullOrWhiteSpace(ev.Title) ? "Calendar Reminder" : ev.Title,
                    Note = ev.Note ?? "",
                    SubjectName = subjName,
                    EventDateTime = eventDt,
                    TriggerDateTime = triggerDt,
                    NotificationId = Math.Abs(StringComparer.Ordinal.GetHashCode(key)) & 0x7FFFFFFF
                });
            }

            if (ev.Notify3DaysBefore)
                AddRule("3_days", "3 days before", eventDt.AddDays(-3));

            if (ev.Notify24HoursBefore)
                AddRule("24_hours", "24 hours before", eventDt.AddHours(-24));

            if (ev.NotifyMorningOf)
                AddRule("morning", $"Morning reminder ({morningTod.Hours:D2}:{morningTod.Minutes:D2})", eventDt.Date + morningTod);

            if (ev.NotifyCustom)
            {
                DateTime customDt;
                if (!string.IsNullOrWhiteSpace(ev.CustomTriggerIso) &&
                    DateTime.TryParse(ev.CustomTriggerIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedCustom))
                {
                    customDt = parsedCustom;
                }
                else
                {
                    int offsetMin = ev.CustomOffsetMinutes > 0
                        ? ev.CustomOffsetMinutes
                        : Math.Max(1, settings.CalendarNotifCustomHours) * 60;
                    customDt = eventDt.AddMinutes(-offsetMin);
                }
                AddRule("custom", $"Custom ({FormatCustomOffsetShort(ev.CustomOffsetMinutes)})", customDt);
            }

            return list;
        }

        public static List<ScheduledTriggerInfo> BuildTriggersForDeadline(DeadlineItem dl, AppStateData state)
        {
            var list = new List<ScheduledTriggerInfo>();
            if (dl == null || dl.Completed) return list;

            var settings = state.Settings ?? new UserSettings();
            if (!settings.CalendarNotifsEnabled) return list;

            var presets = settings.CalendarNotifPresets ?? new List<string>();
            if (presets.Count == 0) return list;

            var eventDt = ParseEventDateTime(dl.Date, dl.Time, new TimeSpan(9, 0, 0));
            var morningTod = ParseTimeOfDay(settings.CalendarNotifStartHour, new TimeSpan(8, 0, 0));
            string subjName = string.IsNullOrWhiteSpace(dl.SubjectId)
                ? ""
                : (state.Subjects.FirstOrDefault(s => s.Id == dl.SubjectId)?.Name ?? "");

            void AddRule(string ruleKey, string ruleLabel, DateTime triggerDt)
            {
                string key = $"dl_{dl.Id}_{ruleKey}_{triggerDt:yyyyMMddHHmm}";
                list.Add(new ScheduledTriggerInfo
                {
                    FiredKey = key,
                    RuleKey = ruleKey,
                    RuleLabel = ruleLabel,
                    EventId = dl.Id,
                    Title = string.IsNullOrWhiteSpace(dl.Title) ? "Subject Deadline" : dl.Title,
                    Note = dl.Notes ?? "",
                    SubjectName = subjName,
                    EventDateTime = eventDt,
                    TriggerDateTime = triggerDt,
                    NotificationId = Math.Abs(StringComparer.Ordinal.GetHashCode(key)) & 0x7FFFFFFF
                });
            }

            if (presets.Contains("3_days"))
                AddRule("3_days", "3 days before", eventDt.AddDays(-3));
            if (presets.Contains("1_day") || presets.Contains("24_hours"))
                AddRule("24_hours", "24 hours before", eventDt.AddHours(-24));
            if (presets.Contains("start_of_day") || presets.Contains("morning"))
                AddRule("morning", $"Morning reminder ({morningTod.Hours:D2}:{morningTod.Minutes:D2})", eventDt.Date + morningTod);
            if (presets.Contains("custom"))
                AddRule("custom", $"Custom (-{Math.Max(1, settings.CalendarNotifCustomHours)}h)", eventDt.AddHours(-Math.Max(1, settings.CalendarNotifCustomHours)));

            return list;
        }

        public static List<ScheduledTriggerInfo> GetAllPlannedTriggers(AppStateData state)
        {
            var all = new List<ScheduledTriggerInfo>();
            if (state == null) return all;

            foreach (var ev in state.CalendarEvents ?? Enumerable.Empty<CalendarEventItem>())
                all.AddRange(BuildTriggersForEvent(ev, state));

            foreach (var dl in state.Deadlines ?? Enumerable.Empty<DeadlineItem>())
                all.AddRange(BuildTriggersForDeadline(dl, state));

            return all.OrderBy(t => t.TriggerDateTime).ToList();
        }

        public static void SuppressPreExistingPastTriggers(CalendarEventItem ev, AppStateData state, DateTime now)
        {
            state.FiredNotificationKeys ??= new List<string>();
            var set = new HashSet<string>(state.FiredNotificationKeys, StringComparer.Ordinal);
            foreach (var trig in BuildTriggersForEvent(ev, state))
            {
                // Suppress alerts that were already from an earlier calendar day before creation,
                // while keeping today's or future triggers eligible.
                if (trig.TriggerDateTime < now.Date)
                {
                    if (set.Add(trig.FiredKey))
                        state.FiredNotificationKeys.Add(trig.FiredKey);
                }
            }
        }

        public static List<ScheduledTriggerInfo> CollectDueNotifications(AppStateData state, DateTime now, bool markAsFired = true)
        {
            var due = new List<ScheduledTriggerInfo>();
            if (state == null) return due;
            var settings = state.Settings ?? new UserSettings();
            if (!settings.CalendarNotifsEnabled) return due;

            state.FiredNotificationKeys ??= new List<string>();
            var firedSet = new HashSet<string>(state.FiredNotificationKeys, StringComparer.Ordinal);

            // Ignore triggers whose event already finished more than 7 days ago
            DateTime cutoffPast = now.Date.AddDays(-7);

            foreach (var trig in GetAllPlannedTriggers(state))
            {
                if (trig.EventDateTime < cutoffPast) continue;
                if (trig.TriggerDateTime > now) continue;
                if (firedSet.Contains(trig.FiredKey)) continue;

                due.Add(trig);
                if (markAsFired && firedSet.Add(trig.FiredKey))
                {
                    state.FiredNotificationKeys.Add(trig.FiredKey);
                }
            }

            if (markAsFired && state.FiredNotificationKeys.Count > 400)
            {
                state.FiredNotificationKeys = state.FiredNotificationKeys
                    .Skip(state.FiredNotificationKeys.Count - 400)
                    .ToList();
            }

            return due;
        }

        public static List<UpcomingEventEntry> GetUpcomingEvents(AppStateData state, bool includeCompleted = false)
        {
            var entries = new List<UpcomingEventEntry>();
            if (state == null) return entries;

            var settings = state.Settings ?? new UserSettings();
            var today = DateTime.Today;
            var now = DateTime.Now;
            var subjectsById = state.Subjects.ToDictionary(s => s.Id, s => s);

            void PopulateCountdown(UpcomingEventEntry item)
            {
                var diffDays = (item.DateTimeLocal.Date - today).Days;
                if (item.Completed)
                {
                    item.RelativeBadge = "DONE";
                    item.CountdownColorHex = "#10B981";
                }
                else if (diffDays < 0 || (diffDays == 0 && item.DateTimeLocal < now.AddHours(-1)))
                {
                    item.IsOverdue = true;
                    item.RelativeBadge = diffDays < 0 ? $"Overdue ({Math.Abs(diffDays)}d)" : "Earlier today";
                    item.CountdownColorHex = "#EF4444";
                }
                else if (diffDays == 0)
                {
                    item.RelativeBadge = "TODAY";
                    item.CountdownColorHex = "#F59E0B";
                }
                else if (diffDays == 1)
                {
                    item.RelativeBadge = "TOMORROW";
                    item.CountdownColorHex = "#06B6D4";
                }
                else if (diffDays <= 3)
                {
                    item.RelativeBadge = $"In {diffDays}d";
                    item.CountdownColorHex = "#A78BFA";
                }
                else
                {
                    item.RelativeBadge = $"In {diffDays}d";
                    item.CountdownColorHex = "#818CF8";
                }
            }

            foreach (var ev in state.CalendarEvents ?? Enumerable.Empty<CalendarEventItem>())
            {
                if (!includeCompleted && ev.Completed) continue;
                var dt = ParseEventDateTime(ev.Date, ev.EventTime, new TimeSpan(9, 0, 0));
                if (!includeCompleted && dt.Date < today.AddDays(-2)) continue;

                subjectsById.TryGetValue(ev.SubjectId ?? "", out var subj);
                var entry = new UpcomingEventEntry
                {
                    Id = ev.Id,
                    Title = string.IsNullOrWhiteSpace(ev.Title) ? "Calendar Note" : ev.Title,
                    Note = ev.Note ?? "",
                    DateTimeLocal = dt,
                    DateKey = dt.ToString("yyyy-MM-dd"),
                    TimeDisplay = dt.ToString("hh:mm tt"),
                    SubjectName = subj?.Name ?? "Personal / General",
                    SubjectIcon = string.IsNullOrWhiteSpace(subj?.Icon) ? "📅" : subj!.Icon,
                    ReminderSummary = FormatReminderSummary(ev, settings),
                    IsDeadlineSource = false,
                    Completed = ev.Completed,
                    SourceCalendarEvent = ev
                };
                PopulateCountdown(entry);
                entries.Add(entry);
            }

            foreach (var dl in state.Deadlines ?? Enumerable.Empty<DeadlineItem>())
            {
                if (!includeCompleted && dl.Completed) continue;
                var dt = ParseEventDateTime(dl.Date, dl.Time, new TimeSpan(9, 0, 0));
                if (!includeCompleted && dt.Date < today.AddDays(-7)) continue;

                subjectsById.TryGetValue(dl.SubjectId ?? "", out var subj);
                var entry = new UpcomingEventEntry
                {
                    Id = dl.Id,
                    Title = string.IsNullOrWhiteSpace(dl.Title) ? "Subject Event" : dl.Title,
                    Note = dl.Notes ?? "",
                    DateTimeLocal = dt,
                    DateKey = dt.ToString("yyyy-MM-dd"),
                    TimeDisplay = dt.ToString("hh:mm tt"),
                    SubjectName = subj?.Name ?? "Subject Event",
                    SubjectIcon = string.IsNullOrWhiteSpace(subj?.Icon) ? "📌" : subj!.Icon,
                    ReminderSummary = settings.CalendarNotifsEnabled ? "Subject schedule" : "Alerts off",
                    IsDeadlineSource = true,
                    Completed = dl.Completed,
                    SourceDeadline = dl
                };
                PopulateCountdown(entry);
                entries.Add(entry);
            }

            return entries
                .OrderBy(e => e.Completed ? 1 : 0)
                .ThenBy(e => e.DateTimeLocal)
                .ToList();
        }
    }
}
