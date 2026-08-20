namespace WordFlow.Infrastructure.Windows;

public sealed class TrayLifecycleController
{
    private static readonly string[] Labels =
        ["Show/Hide Card", "Pause/Resume", "Open Control Center", "Today Progress", "Exit"];
    private readonly Action<bool> setCardVisibility;
    private readonly Action<bool> setPaused;
    private readonly Action openControlCenter;
    private readonly Action showTodayProgress;
    private readonly Action exit;
    private bool cardVisible;
    private bool paused;

    public TrayLifecycleController(
        bool initiallyCardVisible,
        Action<bool> setCardVisibility,
        Action<bool> setPaused,
        Action openControlCenter,
        Action showTodayProgress,
        Action exit)
    {
        cardVisible = initiallyCardVisible;
        this.setCardVisibility = setCardVisibility ?? throw new ArgumentNullException(nameof(setCardVisibility));
        this.setPaused = setPaused ?? throw new ArgumentNullException(nameof(setPaused));
        this.openControlCenter = openControlCenter ?? throw new ArgumentNullException(nameof(openControlCenter));
        this.showTodayProgress = showTodayProgress ?? throw new ArgumentNullException(nameof(showTodayProgress));
        this.exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    public IReadOnlyList<string> ActionLabels => Labels;

    public void SynchronizeCardVisibility(bool isVisible) => cardVisible = isVisible;

    public void Invoke(string actionLabel)
    {
        switch (actionLabel)
        {
            case "Show/Hide Card":
                cardVisible = !cardVisible;
                setCardVisibility(cardVisible);
                break;
            case "Pause/Resume":
                paused = !paused;
                setPaused(paused);
                break;
            case "Open Control Center": openControlCenter(); break;
            case "Today Progress": showTodayProgress(); break;
            case "Exit": exit(); break;
            default: throw new ArgumentOutOfRangeException(nameof(actionLabel), actionLabel, "Unknown tray action.");
        }
    }
}
