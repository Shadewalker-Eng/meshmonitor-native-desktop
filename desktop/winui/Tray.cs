using System.Drawing;
using Forms = System.Windows.Forms;

namespace MeshMonitor.Native;

internal sealed class Tray : IDisposable
{
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem logs;
    private readonly Forms.ToolStripMenuItem data;
    private readonly Forms.NotifyIcon icon;
    private readonly Icon? appIcon;

    internal Tray(Action showNative, Action openLogs, Action openData, Action quit)
    {
        var native = Item("Open MeshMonitor", showNative);
        logs = Item("View logs", openLogs);
        data = Item("Open data folder", openData);
        menu.Items.AddRange([native, new Forms.ToolStripSeparator(), logs, data,
            new Forms.ToolStripSeparator(), Item("Quit MeshMonitor", quit)]);
        IsBackendReady = false;
        if (Environment.ProcessPath is { } path)
            appIcon = Icon.ExtractAssociatedIcon(path);
        icon = new Forms.NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "MeshMonitor",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) showNative();
        };
    }

    internal bool IsBackendReady
    {
        set => logs.Enabled = data.Enabled = value;
    }

    private static Forms.ToolStripMenuItem Item(string label, Action action)
    {
        var item = new Forms.ToolStripMenuItem(label);
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        appIcon?.Dispose();
        menu.Dispose();
    }
}
