using System.Windows.Forms;

namespace ClipVault.App;

/// <summary>
/// The tray icon: Open ClipVault / Lock vault / Start with Windows / Quit,
/// left-click toggles the window. Ported from the <c>TrayIconBuilder</c>
/// setup in <c>lib.rs</c>, plus the Windows-only autostart toggle (see
/// <see cref="Autostart"/>) standing in for the Rust README's manual
/// autostart entry.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    /// <summary>Left-click, or the "Open ClipVault" menu item - both toggle, same as Rust's tray.</summary>
    public event Action? ToggleRequested;
    public event Action? LockRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open ClipVault", null, (_, _) => ToggleRequested?.Invoke());
        menu.Items.Add("Lock vault", null, (_, _) => LockRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = Autostart.IsEnabled(),
        };
        autostart.Click += (_, _) => Autostart.SetEnabled(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ClipVault",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleRequested?.Invoke();
            }
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>
    /// Loads the same .ico embedded in the app for its window and exe icon
    /// (<c>MainWindow.xaml</c>'s <c>Icon</c>, the csproj's <c>ApplicationIcon</c>),
    /// as a WPF resource stream, so there is exactly one icon file to keep in sync.
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/icon.ico");
        var resource = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("Assets/icon.ico is missing from the application resources");
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream);
    }
}
