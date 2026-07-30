using System.Drawing;

namespace LLMAdShield.Windows.Platform.Tray;

internal sealed class TrayIconController : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _shadowModeItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _automaticModeItem;

    public TrayIconController(
        Action show,
        Action selectTarget,
        Action shadowMode,
        Action automaticMode,
        Action pause,
        Action emergencyUnmute,
        Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show LLM AdShield", null, (_, _) => show());
        menu.Items.Add("Select player / audio…", null, (_, _) => selectTarget());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _shadowModeItem = new System.Windows.Forms.ToolStripMenuItem("Shadow mode")
        {
            Checked = true,
            CheckOnClick = false,
        };
        _shadowModeItem.Click += (_, _) => shadowMode();
        menu.Items.Add(_shadowModeItem);

        _automaticModeItem = new System.Windows.Forms.ToolStripMenuItem("Automatic mute")
        {
            CheckOnClick = false,
        };
        _automaticModeItem.Click += (_, _) => automaticMode();
        menu.Items.Add(_automaticModeItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Pause detection", null, (_, _) => pause());
        menu.Items.Add("Emergency unmute", null, (_, _) => emergencyUnmute());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "LLM AdShield — stopped",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void SetAutomaticMode(bool automatic)
    {
        _automaticModeItem.Checked = automatic;
        _shadowModeItem.Checked = !automatic;
    }

    public void SetStatus(string text)
    {
        var value = $"LLM AdShield — {text}";
        _icon.Text = value.Length <= 63 ? value : value[..63];
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
