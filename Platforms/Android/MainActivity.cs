using Android.App;
using Android.Content.PM;
using Android.OS;
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
    }
}
