using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
        // Accessibilité : les boutons-icônes n'ont pas de texte ; leur infobulle sert de nom
        // aux lecteurs d'écran (TalkBack, Narrateur).
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            var name = AutomationProperties.GetName(c);
            if (e.NewValue is string tip && (string.IsNullOrEmpty(name) || name == e.OldValue as string))
                AutomationProperties.SetName(c, tip);
        });
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
