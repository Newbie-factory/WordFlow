using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WordFlow.App.Views.Controls;

public sealed class AccessibleBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
