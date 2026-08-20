using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WordFlow.App.ViewModels;

namespace WordFlow.App.Views.Controls;

public partial class RelationDrawer : UserControl
{
    private double lastDesiredHeight = -1;
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(RelationDrawer), new PropertyMetadata(false));

    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public event EventHandler? DesiredHeightChanged;
    public event EventHandler? DismissRequested;
    public RelationDrawer()
    {
        InitializeComponent();
        Loaded += (_, _) => ScheduleViewportMeasure();
        SizeChanged += (_, _) => ScheduleViewportMeasure();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ScheduleViewportMeasure() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, MeasureViewport);

    private void MeasureViewport()
    {
        if (!IsLoaded) return;
        var rows = Descendants(this).OfType<FrameworkElement>()
            .Where(element => element.Name == "RelationRowSurface" && element.ActualHeight > 0)
            .Select(element => element.ActualHeight).ToArray();
        double groupHeaders = Descendants(this).OfType<FrameworkElement>()
            .Count(element => element.Name == "SenseGroupExpander" && element.IsVisible) * 20;
        double available = double.IsFinite(MaxHeight)
            ? Math.Max(0, MaxHeight - HeaderPanel.ActualHeight - FooterPanel.ActualHeight - 8)
            : 420;
        double measured = RelationDrawerViewport.Measure(rows, available, 6);
        ResultsScrollViewer.MaxHeight = measured > 0 ? Math.Min(available, measured + groupHeaders) : Math.Min(available, 72);
        var desired = DesiredFiveRowHeight();
        if (!IsCompact && Math.Abs(lastDesiredHeight - desired) > 0.5)
        {
            lastDesiredHeight = desired;
            DesiredHeightChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double DesiredFiveRowHeight()
    {
        var rows = Descendants(this).OfType<FrameworkElement>()
            .Where(element => element.Name == "RelationRowSurface" && element.ActualHeight > 0)
            .Select(element => element.ActualHeight).ToArray();
        double groupHeaders = Descendants(this).OfType<FrameworkElement>()
            .Count(element => element.Name == "SenseGroupExpander" && element.IsVisible) * 20;
        var results = RelationDrawerViewport.Measure(rows, double.MaxValue, 6);
        return Math.Max(96, HeaderPanel.ActualHeight + FooterPanel.ActualHeight + groupHeaders + results + 40);
    }

    public void FocusSearch()
    {
        RelationSearchBox.Focus();
        Keyboard.Focus(RelationSearchBox);
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
    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
