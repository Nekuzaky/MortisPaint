using Avalonia.Controls;

namespace MortisPaint.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => MainView.Current?.OnAppClosing();
    }
}
