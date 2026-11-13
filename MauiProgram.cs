using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace StudyHub;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        SQLitePCL.Batteries_V2.Init();
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<StudyHubMauiApp>();
        return builder.Build();
    }
}
