using System.Windows;

namespace WordFlow.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public void SetPaused(bool paused) => StatusText.Text = paused ? "Paused" : "Offline corpus verified";
}
