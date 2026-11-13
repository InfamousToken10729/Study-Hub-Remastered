using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace StudyHub;

public partial class StudyHubMauiApp : Application
{
    public StudyHubMauiApp()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MauiMainPage())
        {
            Title = "Study Hub v3.0 — Remastered Edition"
        };
    }
}
