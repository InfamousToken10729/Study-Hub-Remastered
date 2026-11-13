using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using StudyHub.Models;
using StudyHub.Services;

namespace StudyHub.Platforms.Android
{
    public static class AndroidCalendarNotifications
    {
        public const string ChannelId = "studyhub_calendar_reminders";
        public const string ActionCalendarAlarm = "com.studyhub.remastered.ACTION_CALENDAR_ALARM";
        private const string PrefsName = "StudyHubCalNotifsPrefs";
        private const string PrefFiredKeys = "fired_keys";
        private const int MaxStoredAlarmIds = 64;

        private static CalendarUnlockReceiver? _unlockReceiver;
        private static readonly object _syncLock = new();

        public static event Action<List<string>>? BackgroundKeysFired;

        public static void RegisterDynamicUnlockReceiver(Context context)
        {
            lock (_syncLock)
            {
                if (_unlockReceiver != null) return;
                try
                {
                    _unlockReceiver = new CalendarUnlockReceiver();
                    var filter = new IntentFilter();
                    filter.AddAction(Intent.ActionUserPresent);
                    filter.AddAction(Intent.ActionScreenOn);
                    if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    {
                        context.ApplicationContext?.RegisterReceiver(_unlockReceiver, filter, ReceiverFlags.NotExported);
                    }
                    else
                    {
                        context.ApplicationContext?.RegisterReceiver(_unlockReceiver, filter);
                    }
                }
                catch
                {
                    // Ignore receiver registration failure
                }
            }
        }

        public static void EnsureNotificationChannel(Context context)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
            try
            {
                var mgr = context.GetSystemService(Context.NotificationService) as NotificationManager;
                if (mgr == null) return;
                if (mgr.GetNotificationChannel(ChannelId) != null) return;

                var channel = new NotificationChannel(ChannelId, "StudyHub Calendar Reminders", NotificationImportance.High)
                {
                    Description = "Upcoming study events, notes, and morning reminders"
                };
                channel.EnableVibration(true);
                channel.LockscreenVisibility = NotificationVisibility.Public;
                mgr.CreateNotificationChannel(channel);
            }
            catch { }
        }

        public static List<ScheduledTriggerInfo> CheckAndDispatchDue(Context context, AppStateData? liveState = null, StorageService? liveStorage = null)
        {
            var firedNow = new List<ScheduledTriggerInfo>();
            lock (_syncLock)
            {
                try
                {
                    EnsureNotificationChannel(context);

                    var storage = liveStorage ?? new StorageService();
                    var state = liveState ?? storage.LoadState();
                    if (state?.Settings == null || !state.Settings.CalendarNotifsEnabled)
                        return firedNow;

                    var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                    var prefFired = prefs?.GetStringSet(PrefFiredKeys, new List<string>()) ?? new List<string>();
                    state.FiredNotificationKeys ??= new List<string>();

                    var combinedFired = new HashSet<string>(state.FiredNotificationKeys, StringComparer.Ordinal);
                    foreach (var pk in prefFired) combinedFired.Add(pk);
                    state.FiredNotificationKeys = combinedFired.ToList();

                    var due = CalendarNotificationEngine.CollectDueNotifications(state, DateTime.Now, markAsFired: true);
                    if (due.Count == 0) return firedNow;

                    var firedKeys = new List<string>();
                    foreach (var trig in due)
                    {
                        PostAndroidNotification(context, trig);
                        firedNow.Add(trig);
                        firedKeys.Add(trig.FiredKey);
                    }

                    // Save updated fired keys to SharedPreferences & storage
                    if (prefs != null)
                    {
                        var updatedSet = new HashSet<string>(state.FiredNotificationKeys.TakeLast(400), StringComparer.Ordinal);
                        var ed = prefs.Edit();
                        ed?.PutStringSet(PrefFiredKeys, updatedSet);
                        ed?.Apply();
                    }

                    if (liveState == null)
                    {
                        storage.SaveStateSync(state);
                    }

                    BackgroundKeysFired?.Invoke(firedKeys);
                }
                catch { }
            }
            return firedNow;
        }

        public static void RescheduleAll(Context context, AppStateData state)
        {
            try
            {
                EnsureNotificationChannel(context);
                var alarmMgr = context.GetSystemService(Context.AlarmService) as AlarmManager;
                if (alarmMgr == null) return;

                // Cancel previously scheduled slot request codes
                for (int i = 0; i < MaxStoredAlarmIds; i++)
                {
                    var cancelIntent = new Intent(context, typeof(CalendarNotificationReceiver));
                    cancelIntent.SetAction(ActionCalendarAlarm);
                    int flags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                        ? (int)(PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable)
                        : (int)PendingIntentFlags.NoCreate;
                    var existingPi = PendingIntent.GetBroadcast(context, 41000 + i, cancelIntent, (PendingIntentFlags)flags);
                    if (existingPi != null)
                    {
                        alarmMgr.Cancel(existingPi);
                        existingPi.Cancel();
                    }
                }

                if (state?.Settings == null || !state.Settings.CalendarNotifsEnabled) return;

                var now = DateTime.Now;
                var firedSet = new HashSet<string>(state.FiredNotificationKeys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                var upcomingTriggers = CalendarNotificationEngine.GetAllPlannedTriggers(state)
                    .Where(t => t.TriggerDateTime > now && !firedSet.Contains(t.FiredKey))
                    .Take(MaxStoredAlarmIds)
                    .ToList();

                bool canExact = true;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                {
                    canExact = alarmMgr.CanScheduleExactAlarms();
                }

                for (int i = 0; i < upcomingTriggers.Count; i++)
                {
                    var trig = upcomingTriggers[i];
                    long triggerAtMs = new DateTimeOffset(trig.TriggerDateTime).ToUnixTimeMilliseconds();

                    var alarmIntent = new Intent(context, typeof(CalendarNotificationReceiver));
                    alarmIntent.SetAction(ActionCalendarAlarm);
                    alarmIntent.PutExtra("fired_key", trig.FiredKey);

                    int flags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                        ? (int)(PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
                        : (int)PendingIntentFlags.UpdateCurrent;
                    var pi = PendingIntent.GetBroadcast(context, 41000 + i, alarmIntent, (PendingIntentFlags)flags);
                    if (pi == null) continue;

                    if (canExact && Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    {
                        alarmMgr.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMs, pi);
                    }
                    else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    {
                        alarmMgr.SetAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAtMs, pi);
                    }
                    else
                    {
                        alarmMgr.Set(AlarmType.RtcWakeup, triggerAtMs, pi);
                    }
                }
            }
            catch { }
        }

        private static void PostAndroidNotification(Context context, ScheduledTriggerInfo trig)
        {
            try
            {
                var mgr = context.GetSystemService(Context.NotificationService) as NotificationManager;
                if (mgr == null) return;

                var launchIntent = new Intent(context, typeof(MainActivity));
                launchIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
                int piFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                    ? (int)(PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
                    : (int)PendingIntentFlags.UpdateCurrent;
                var contentPi = PendingIntent.GetActivity(context, trig.NotificationId, launchIntent, (PendingIntentFlags)piFlags);

                string subPrefix = string.IsNullOrWhiteSpace(trig.SubjectName) ? "" : $"[{trig.SubjectName}] ";
                string contentTitle = $"📅 {trig.Title}";
                string summaryLine = $"{subPrefix}{trig.RuleLabel} • {trig.EventDateTime:MMM dd, hh:mm tt}";
                string bigText = string.IsNullOrWhiteSpace(trig.Note)
                    ? summaryLine
                    : $"{summaryLine}\n{trig.Note}";

                var builder = new NotificationCompat.Builder(context, ChannelId)
                    .SetSmallIcon(global::StudyHub.Resource.Mipmap.appicon)
                    .SetContentTitle(contentTitle)
                    .SetContentText(summaryLine)
                    .SetStyle(new NotificationCompat.BigTextStyle().BigText(bigText))
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .SetCategory(NotificationCompat.CategoryReminder)
                    .SetVisibility(NotificationCompat.VisibilityPublic)
                    .SetAutoCancel(true)
                    .SetDefaults(NotificationCompat.DefaultAll)
                    .SetContentIntent(contentPi);

                mgr.Notify(trig.NotificationId, builder.Build());
            }
            catch { }
        }
    }

    [BroadcastReceiver(Name = "com.studyhub.remastered.CalendarNotificationReceiver", Enabled = true, Exported = false)]
    [IntentFilter(new[]
    {
        Intent.ActionBootCompleted,
        Intent.ActionMyPackageReplaced,
        AndroidCalendarNotifications.ActionCalendarAlarm
    })]
    public class CalendarNotificationReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null) return;
            try
            {
                AndroidCalendarNotifications.RegisterDynamicUnlockReceiver(context);
                AndroidCalendarNotifications.CheckAndDispatchDue(context);
                var storage = new StorageService();
                AndroidCalendarNotifications.RescheduleAll(context, storage.LoadState());
            }
            catch { }
        }
    }

    public class CalendarUnlockReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null) return;
            try
            {
                AndroidCalendarNotifications.CheckAndDispatchDue(context);
            }
            catch { }
        }
    }
}
