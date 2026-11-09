using System;
using System.IO;
using System.Windows;

namespace StudyHub
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                File.WriteAllText("crash_log.txt", args.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                File.WriteAllText("crash_log.txt", args.Exception?.ToString() ?? "Unknown dispatcher exception");
                args.Handled = true;
            };
        }
    }
}
