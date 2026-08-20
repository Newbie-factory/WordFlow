using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace WordFlow.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private int disposed;

    public TrayIconService(TrayLifecycleController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var menu = new ContextMenuStrip();
        foreach (string label in controller.ActionLabels)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => controller.Invoke(label);
            menu.Items.Add(item);
        }

        notifyIcon = new NotifyIcon
        {
            Text = "WordFlow",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => controller.Invoke("Show/Hide Card");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
    }
}
