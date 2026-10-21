using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MortisPaint.Controls;
using MortisPaint.Engine;
using MortisPaint.Services;
using MortisPaint.Views;

namespace MortisPaint;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        BrushTips.Directory = System.IO.Path.Combine(AppPaths.Root, "Pointes");
        uint accent = AppSettings.Data.Accent;
        MortisTheme.Apply(accent == 0 ? MortisTheme.Accents[0].Color : Color.FromUInt32(accent));
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
