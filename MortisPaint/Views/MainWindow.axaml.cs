using Avalonia.Controls;

namespace MortisPaint.Views;

public partial class MainWindow : Window
{
    bool _leaveUnsaved;

    public MainWindow()
    {
        InitializeComponent();
        Closing += async (_, e) =>
        {
            if (_leaveUnsaved || MainView.Current is not { } main) return;
            var error = main.OnAppClosing();
            if (error == null) return;
            // L'enregistrement a échoué : on garde la fenêtre ouverte et on demande.
            e.Cancel = true;
            if (!await main.ConfirmUnsavedAsync(error, "Quitter sans enregistrer")) return;
            _leaveUnsaved = true;
            Close();
        };
    }
}
