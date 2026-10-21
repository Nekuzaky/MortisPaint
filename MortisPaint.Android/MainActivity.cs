using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace MortisPaint.Android;

[Activity(
    Label = "MortisPaint",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    // Rotation, pliage, clavier... ne recréent pas l'activité : le dessin en cours n'est pas perdu.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Density
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity
{
}
