using Android.App;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace StudyHub;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        try
        {
            StudyHub.Platforms.Android.AndroidCalendarNotifications.EnsureNotificationChannel(this);
            StudyHub.Platforms.Android.AndroidCalendarNotifications.RegisterDynamicUnlockReceiver(this);
        }
        catch { }
    }
}
