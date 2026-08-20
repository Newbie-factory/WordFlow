using System.Windows;
using System.Windows.Controls;
using WordFlow.App.ViewModels;

namespace WordFlow.App.Views.Controls;

public partial class RelationDrawer : UserControl
{
    public RelationDrawer() => InitializeComponent();

    private static RelationItemViewModel? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as RelationItemViewModel;

    private void Speak_Click(object sender, RoutedEventArgs args) => ItemFrom(sender)?.RequestSpeak();
    private void Details_Click(object sender, RoutedEventArgs args) => ItemFrom(sender)?.RequestDetails();
    private void AddToLearning_Click(object sender, RoutedEventArgs args) => ItemFrom(sender)?.RequestAddToLearning();
}
