using WordFlow.Infrastructure.Windows;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class TrayLifecycleControllerTests
{
    [Fact]
    public void Exposes_the_exact_tray_actions()
    {
        var controller = CreateController();

        Assert.Equal(
            ["Show/Hide Card", "Pause/Resume", "Open Control Center", "Today Progress", "Exit"],
            controller.ActionLabels);
    }

    [Fact]
    public void Show_hide_and_pause_resume_toggle_state_and_invoke_callbacks()
    {
        List<bool> cardStates = [];
        List<bool> pauseStates = [];
        var controller = new TrayLifecycleController(
            initiallyCardVisible: true,
            cardStates.Add,
            pauseStates.Add,
            () => { }, () => { }, () => { });

        controller.Invoke("Show/Hide Card");
        controller.Invoke("Pause/Resume");
        controller.Invoke("Show/Hide Card");
        controller.Invoke("Pause/Resume");

        Assert.Equal([false, true], cardStates);
        Assert.Equal([true, false], pauseStates);
    }

    [Fact]
    public void Explicit_actions_are_dispatched_once_and_unknown_actions_are_rejected()
    {
        int controlCenter = 0, progress = 0, exit = 0;
        var controller = new TrayLifecycleController(false, _ => { }, _ => { },
            () => controlCenter++, () => progress++, () => exit++);

        controller.Invoke("Open Control Center");
        controller.Invoke("Today Progress");
        controller.Invoke("Exit");

        Assert.Equal((1, 1, 1), (controlCenter, progress, exit));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Invoke("Download corpus"));
    }

    [Fact]
    public void Window_close_hide_is_synchronized_so_the_next_toggle_shows_the_card()
    {
        List<bool> cardStates = [];
        var controller = new TrayLifecycleController(
            initiallyCardVisible: true, cardStates.Add, _ => { }, () => { }, () => { }, () => { });

        controller.SynchronizeCardVisibility(isVisible: false);
        controller.Invoke("Show/Hide Card");

        Assert.Equal([true], cardStates);
    }

    private static TrayLifecycleController CreateController() =>
        new(true, _ => { }, _ => { }, () => { }, () => { }, () => { });
}
