using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WordFlow.App.ViewModels;

namespace WordFlow.App.Views.Controls;

public partial class RelationDrawer : UserControl
{
    public RelationDrawer()
    {
        InitializeComponent();
        Loaded += (_, _) => ScheduleViewportMeasure();
        SizeChanged += (_, _) => ScheduleViewportMeasure();
    }

    public void ScheduleViewportMeasure() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, MeasureViewport);

    private void MeasureViewport()
    {
        if (!IsLoaded) return;
        var rows = Descendants(this).OfType<FrameworkElement>()
            .Where(element => element.Name == "RelationRowSurface" && element.ActualHeight > 0)
            .Select(element => element.ActualHeight).ToArray();
        double groupHeaders = Descendants(this).OfType<FrameworkElement>()
            .Where(element => element.Name == "SenseGroupHeader" && element.IsVisible)
            .Sum(element => element.ActualHeight + 6);
        double available = double.IsFinite(MaxHeight)
            ? Math.Max(72, MaxHeight - HeaderPanel.ActualHeight - FooterPanel.ActualHeight - 8)
            : 420;
        double measured = RelationDrawerViewport.Measure(rows, available, 6);
        ResultsScrollViewer.MaxHeight = measured > 0 ? Math.Min(available, measured + groupHeaders) : Math.Min(available, 72);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static RelationItemViewModel? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as RelationItemViewModel;

    private void Speak_Click(object sender, RoutedEventArgs args) => ItemFrom(sender)?.RequestSpeak();
    private void Details_Click(object sender, RoutedEventArgs args) => ItemFrom(sender)?.RequestDetails();
    private void AddToLearning_Click(object sender, RoutedEventArgs args) => ItemFrom(sender)?.RequestAddToLearning();
}
