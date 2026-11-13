using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Microsoft.Maui;

namespace StudyHub;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Window != null)
        {
            Window.SetStatusBarColor(Android.Graphics.Color.ParseColor("#090B1A"));
            Window.SetNavigationBarColor(Android.Graphics.Color.ParseColor("#090B1A"));
        }

        try
        {
            StudyHub.Platforms.Android.AndroidCalendarNotifications.EnsureNotificationChannel(this);
            StudyHub.Platforms.Android.AndroidCalendarNotifications.RegisterDynamicUnlockReceiver(this);

            if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
                ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(this, new[] { Manifest.Permission.PostNotifications }, 1001);
            }
        }
        catch { }
    }

    protected override void OnResume()
    {
        base.OnResume();
        try
        {
            StudyHub.Platforms.Android.AndroidCalendarNotifications.RegisterDynamicUnlockReceiver(this);
        }
        catch { }
    }
}
