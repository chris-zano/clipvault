using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipVault.App.Clipboard;
using ClipVault.Core;
// UseWindowsForms (for the tray icon) brings System.Windows.Forms and
// System.Drawing into scope too, which collide with these WPF types.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace ClipVault.App;

/// <summary>
/// The history and vault UI. A close port of <c>src/main.ts</c>: same tabs,
/// same keyboard model, same confirmations - rebuilding each list from
/// scratch on every refresh rather than diffing, the same way the TS version
/// replaces the whole <c>&lt;ul&gt;</c> each time.
/// </summary>
public partial class MainWindow : Window
{
    private enum Tab { History, Vault }

    private readonly record struct VaultStatusView(bool Exists, bool Unlocked, long? SecondsUntilLock);

    private readonly AppState _state;
    private readonly ClipboardWriter _writer;

    private Tab _tab = Tab.History;
    private List<Entry> _entries = [];
    private int _selected;

    private List<VaultItem> _vaultItems = [];
    private VaultStatusView _vaultStatus = new(false, false, null);

    /// <summary>label/secret pairs currently shown in place of the bullets, until their timer clears them.</summary>
    private readonly Dictionary<long, string> _revealedSecrets = new();

    private readonly DispatcherTimer _countdownTimer;
    private DispatcherTimer? _statusClearTimer;
    private string? _statusMessage;

    internal MainWindow(AppState state, ClipboardWriter writer)
    {
        InitializeComponent();
        _state = state;
        _writer = writer;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => TickCountdown();
        _countdownTimer.Start();

        RefreshHistory();
        Closed += (_, _) => _countdownTimer.Stop();
    }

    /// <summary>Called every time the window is shown. Mirrors the <c>window-shown</c> event in <c>main.ts</c>.</summary>
    public void OnShown()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        RefreshHistory();
    }

    /// <summary>A new entry was recorded. Mirrors the <c>history-changed</c> event.</summary>
    public void NotifyHistoryChanged()
    {
        if (_tab == Tab.History)
        {
            RefreshHistory();
        }
    }

    /// <summary>The auto-lock sweeper locked the vault. Mirrors the <c>vault-locked</c> event.</summary>
    public void NotifyVaultLocked()
    {
        if (_tab == Tab.Vault)
        {
            RefreshVault();
        }
        Say("Vault locked");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ---- status line -------------------------------------------------

    private void Say(string message, int ms = 2200)
    {
        _statusMessage = message;
        StatusText.Text = message;
        _statusClearTimer?.Stop();
        if (ms <= 0)
        {
            return;
        }
        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer!.Stop();
            if (StatusText.Text == _statusMessage)
            {
                StatusText.Text = "";
            }
        };
        _statusClearTimer.Start();
    }

    // ---- tabs and keyboard ---------------------------------------------

    private void OnHistoryTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.History);

    private void OnVaultTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Vault);

    private void SwitchTab(Tab next)
    {
        _tab = next;
        HistoryTabButton.IsChecked = next == Tab.History;
        VaultTabButton.IsChecked = next == Tab.Vault;
        HistoryView.Visibility = next == Tab.History ? Visibility.Visible : Visibility.Collapsed;
        VaultView.Visibility = next == Tab.Vault ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = next == Tab.History ? Visibility.Visible : Visibility.Collapsed;

        if (next == Tab.Vault)
        {
            RefreshVault();
        }
        else
        {
            RefreshHistory();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab)
        {
            SwitchTab(_tab == Tab.History ? Tab.Vault : Tab.History);
            e.Handled = true;
            return;
        }

        if (_tab != Tab.History || _entries.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            _selected = Math.Min(_selected + 1, _entries.Count - 1);
            HighlightSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            _selected = Math.Max(_selected - 1, 0);
            HighlightSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CopyEntry(_entries[_selected].Id);
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var cur = _entries[_selected];
            _state.Store.SetPinned(cur.Id, !cur.Pinned);
            RefreshHistory();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteEntry(_entries[_selected]);
            e.Handled = true;
        }
    }

    // ---- history ---------------------------------------------------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _selected = 0;
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        _entries = _state.Store.List(SearchBox.Text, 300);
        if (_selected >= _entries.Count)
        {
            _selected = Math.Max(0, _entries.Count - 1);
        }
        RenderHistory();
    }

    private void RenderHistory()
    {
        HistoryEmptyText.Visibility = _entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Children.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            HistoryList.Children.Add(BuildHistoryRow(_entries[i], i));
        }
    }

    private Border BuildHistoryRow(Entry entry, int index)
    {
        var row = new Border
        {
            Padding = new Thickness(11, 9, 11, 9),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 4),
            Background = (Brush)FindResource("PanelColor"),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
        };
        row.MouseEnter += (_, _) => { _selected = index; HighlightSelection(); };

        var content = new DockPanel();

        if (entry.Kind == "image" && entry.Thumb is not null)
        {
            var thumb = new Image
            {
                Width = 54,
                Height = 40,
                Stretch = Stretch.UniformToFill,
                Source = DecodeDataUrl(entry.Thumb),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0),
            };
            thumb.MouseLeftButtonUp += (_, _) => CopyEntry(entry.Id);
            AutomationProperties.SetAutomationId(thumb, $"EntryThumb_{entry.Id}");
            DockPanel.SetDock(thumb, Dock.Left);
            content.Children.Add(thumb);
        }

        var actions = BuildHistoryActions(entry);
        DockPanel.SetDock(actions, Dock.Right);
        content.Children.Add(actions);

        var meta = new TextBlock
        {
            Text = $"{SizeText(entry.Bytes)} · {AgoText(entry.LastUsedAt)}",
            Foreground = (Brush)FindResource("FgDimColor"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };
        DockPanel.SetDock(meta, Dock.Right);
        content.Children.Add(meta);

        var preview = new TextBlock
        {
            Text = entry.Preview + (entry.Truncated ? " …" : ""),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            FontFamily = entry.Kind == "image" ? new FontFamily("Segoe UI") : new FontFamily("Consolas"),
            Foreground = (Brush)FindResource(entry.Kind == "image" ? "FgDimColor" : "FgColor"),
            FontSize = entry.Kind == "image" ? 13 : 12.5,
        };
        preview.MouseLeftButtonUp += (_, _) => CopyEntry(entry.Id);
        AutomationProperties.SetAutomationId(preview, $"EntryPreview_{entry.Id}");
        content.Children.Add(preview);

        row.Child = content;
        return row;
    }

    private StackPanel BuildHistoryActions(Entry entry)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var pin = SmallButton(entry.Pinned ? "★" : "☆", entry.Pinned ? "Unpin" : "Pin");
        if (entry.Pinned)
        {
            pin.Foreground = (Brush)FindResource("PinColor");
        }
        pin.Click += (_, _) =>
        {
            _state.Store.SetPinned(entry.Id, !entry.Pinned);
            RefreshHistory();
        };
        AutomationProperties.SetAutomationId(pin, $"EntryPin_{entry.Id}");
        actions.Children.Add(pin);

        if (entry.Kind == "text")
        {
            var stash = SmallButton("🔒", "Move into vault");
            stash.Click += (_, _) => AbsorbIntoVault(entry);
            AutomationProperties.SetAutomationId(stash, $"EntryStash_{entry.Id}");
            actions.Children.Add(stash);
        }

        var delete = SmallButton("✕", "Delete");
        delete.Foreground = (Brush)FindResource("DangerColor");
        delete.Click += (_, _) => DeleteEntry(entry);
        AutomationProperties.SetAutomationId(delete, $"EntryDelete_{entry.Id}");
        actions.Children.Add(delete);

        return actions;
    }

    private void HighlightSelection()
    {
        for (var i = 0; i < HistoryList.Children.Count; i++)
        {
            if (HistoryList.Children[i] is Border row)
            {
                row.BorderBrush = i == _selected ? (Brush)FindResource("AccentColor") : Brushes.Transparent;
                row.Background = i == _selected ? (Brush)FindResource("PanelHiColor") : (Brush)FindResource("PanelColor");
            }
        }
    }

    private void CopyEntry(long id)
    {
        try
        {
            if (_state.Store.Kind(id) == "image")
            {
                var png = _state.Store.ImagePng(id);
                if (png is not null)
                {
                    _writer.SetImagePng(png);
                }
            }
            else
            {
                var content = _state.Store.Content(id);
                if (content is not null)
                {
                    _writer.SetText(content);
                }
            }
            _state.Store.MarkUsed(id);
        }
        catch (Exception ex)
        {
            Say(ex.Message);
            return;
        }
        Hide();
        RefreshHistory();
    }

    /// <summary>
    /// Deleting a history entry is immediate and unrecoverable, and an image
    /// is usually the only copy of a screenshot, so it asks first.
    /// </summary>
    private void DeleteEntry(Entry entry)
    {
        var what = entry.Kind == "image"
            ? $"this image ({entry.Width}x{entry.Height})"
            : $"\"{Truncate(entry.Preview, 60)}\"";
        if (MessageBox.Show(this, $"Delete {what}?\n\nThis cannot be undone.", "Delete?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        _state.Store.Delete(entry.Id);
        Say("Deleted");
        RefreshHistory();
    }

    private void AbsorbIntoVault(Entry entry)
    {
        if (_state.Store.VaultMeta() is null)
        {
            Say("Set up the vault first (Vault tab)");
            SwitchTab(Tab.Vault);
            return;
        }
        if (!_state.Vault.IsUnlocked)
        {
            Say("Unlock the vault first");
            SwitchTab(Tab.Vault);
            return;
        }
        var label = PromptDialog.ShowSingle(this, "Label for this secret:", "Label", Truncate(entry.Preview, 40));
        if (label is null)
        {
            return;
        }
        try
        {
            // Mirrors `vault_absorb_entry` in commands.rs: encrypt, then
            // remove the plaintext row so the secret exists in one place only.
            var content = _state.Store.Content(entry.Id) ?? throw new VaultException("entry no longer exists");
            var vault = _state.VaultChecked();
            var (nonce, ct) = vault.Encrypt(content);
            _state.Store.VaultInsert(label, nonce, ct);
            _state.Store.Delete(entry.Id);
            Say("Moved into the vault");
            RefreshHistory();
        }
        catch (Exception ex)
        {
            Say(ex.Message);
        }
    }

    // ---- vault -------------------------------------------------------

    private void RefreshVault()
    {
        _state.VaultChecked();
        var exists = _state.Store.VaultMeta() is not null;
        _vaultStatus = new VaultStatusView(exists, _state.Vault.IsUnlocked, _state.Vault.SecondsUntilLock(Vault.AutoLockMinutes));

        VaultGatePanel.Visibility = _vaultStatus.Unlocked ? Visibility.Collapsed : Visibility.Visible;
        VaultOpenPanel.Visibility = _vaultStatus.Unlocked ? Visibility.Visible : Visibility.Collapsed;

        GateTitleText.Text = exists ? "Unlock vault" : "Create your vault";
        GateHintText.Text = exists
            ? "Locked. Auto-locks after 10 idle minutes."
            : "Pick a master password. It is never stored - it derives the key that encrypts your secrets, so there is no way to recover it if you forget it.";
        GatePassword2Box.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        GateSubmitButton.Content = exists ? "Unlock" : "Create vault";

        if (_vaultStatus.Unlocked)
        {
            _vaultItems = _state.Store.VaultList();
            RenderVault();
        }
        UpdateTimerText();
    }

    private void RenderVault()
    {
        VaultEmptyText.Visibility = _vaultItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        VaultList.Children.Clear();
        foreach (var item in _vaultItems)
        {
            VaultList.Children.Add(BuildVaultRow(item));
        }
    }

    private Border BuildVaultRow(VaultItem item)
    {
        var row = new Border
        {
            Padding = new Thickness(11, 9, 11, 9),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 4),
            Background = (Brush)FindResource("PanelColor"),
        };

        var content = new DockPanel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var copy = SmallButton("copy", "Copy to clipboard (not recorded in history)");
        copy.Click += (_, _) => VaultCopy(item);
        AutomationProperties.SetAutomationId(copy, $"VaultCopy_{item.Id}");
        actions.Children.Add(copy);

        var reveal = SmallButton("reveal", "Reveal for 10 seconds");
        reveal.Click += (_, _) => VaultReveal(item.Id);
        AutomationProperties.SetAutomationId(reveal, $"VaultReveal_{item.Id}");
        actions.Children.Add(reveal);

        var delete = SmallButton("✕", "Delete");
        delete.Foreground = (Brush)FindResource("DangerColor");
        delete.Click += (_, _) => VaultDelete(item);
        AutomationProperties.SetAutomationId(delete, $"VaultDelete_{item.Id}");
        actions.Children.Add(delete);

        DockPanel.SetDock(actions, Dock.Right);
        content.Children.Add(actions);

        var secretText = new TextBlock
        {
            Text = _revealedSecrets.TryGetValue(item.Id, out var revealed) ? Truncate(revealed, 48) : "••••••••",
            Foreground = (Brush)FindResource("FgDimColor"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };
        AutomationProperties.SetAutomationId(secretText, $"VaultSecret_{item.Id}");
        DockPanel.SetDock(secretText, Dock.Right);
        content.Children.Add(secretText);

        var label = new TextBlock
        {
            Text = item.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("FgColor"),
        };
        content.Children.Add(label);

        row.Child = content;
        return row;
    }

    private void UpdateTimerText()
    {
        VaultTimerText.Text = _vaultStatus is { Unlocked: true, SecondsUntilLock: { } s }
            ? $"locks in {s / 60}:{s % 60:D2}"
            : "";
    }

    private void TickCountdown()
    {
        if (_tab != Tab.Vault || _vaultStatus is not { Unlocked: true, SecondsUntilLock: { } s })
        {
            return;
        }
        _vaultStatus = _vaultStatus with { SecondsUntilLock = Math.Max(0, s - 1) };
        UpdateTimerText();
    }

    private void OnGateSubmit(object sender, RoutedEventArgs e)
    {
        GateErrorText.Text = "";
        var password = GatePasswordBox.Password;
        var password2 = GatePassword2Box.Password;
        try
        {
            if (!_vaultStatus.Exists)
            {
                if (password != password2)
                {
                    GateErrorText.Text = "Passwords do not match";
                    return;
                }
                _state.Vault.Init(_state.Store, password);
            }
            else
            {
                _state.Vault.Unlock(_state.Store, password);
            }
            GatePasswordBox.Password = "";
            GatePassword2Box.Password = "";
            RefreshVault();
        }
        catch (Exception ex)
        {
            GateErrorText.Text = ex.Message;
        }
    }

    private void OnVaultNewClick(object sender, RoutedEventArgs e)
    {
        var fields = PromptDialog.ShowDouble(this, "Add a new secret", "Label", "Secret value");
        if (fields is not { } f)
        {
            return;
        }
        try
        {
            var vault = _state.VaultChecked();
            var (nonce, ct) = vault.Encrypt(f.Field2);
            _state.Store.VaultInsert(f.Field1, nonce, ct);
            RefreshVault();
        }
        catch (Exception ex)
        {
            Say(ex.Message);
        }
    }

    private void OnVaultLockClick(object sender, RoutedEventArgs e)
    {
        _state.Vault.Lock();
        RefreshVault();
    }

    private void VaultCopy(VaultItem item)
    {
        try
        {
            var (nonce, ct) = _state.Store.VaultGet(item.Id) ?? throw new VaultException("vault entry no longer exists");
            var vault = _state.VaultChecked();
            var secret = vault.Decrypt(nonce, ct);
            vault.Touch();
            _writer.SetSecretText(secret);
            Say("Copied — not saved to history");
            RefreshVault();
        }
        catch (Exception ex)
        {
            Say(ex.Message);
        }
    }

    private void VaultReveal(long id)
    {
        try
        {
            var (nonce, ct) = _state.Store.VaultGet(id) ?? throw new VaultException("vault entry no longer exists");
            var vault = _state.VaultChecked();
            var secret = vault.Decrypt(nonce, ct);
            vault.Touch();
            _revealedSecrets[id] = secret;
            RenderVault();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _revealedSecrets.Remove(id);
                if (_tab == Tab.Vault)
                {
                    RenderVault();
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Say(ex.Message);
        }
    }

    private void VaultDelete(VaultItem item)
    {
        if (MessageBox.Show(this, $"Delete \"{item.Label}\" from the vault? This cannot be undone.", "Delete?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        _state.Store.VaultDelete(item.Id);
        RefreshVault();
    }

    // ---- formatting helpers -------------------------------------------

    /// <summary>
    /// Matches the web UI's own `size()` helper exactly, including its one
    /// quirk: no MB tier, so a multi-megabyte image shows as e.g. "2048.0 KB"
    /// here even though the stored preview text (built server-side, with its
    /// own 3-tier `human_size`) would say "2.0 MB".
    /// </summary>
    private static string SizeText(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:F1} KB";

    private static string AgoText(long unixSeconds)
    {
        var s = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds);
        if (s < 60) return "just now";
        if (s < 3600) return $"{s / 60}m ago";
        if (s < 86400) return $"{s / 3600}h ago";
        return $"{s / 86400}d ago";
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    private static Button SmallButton(string text, string tooltip) => new()
    {
        Content = text,
        ToolTip = tooltip,
        Padding = new Thickness(5, 2, 5, 2),
        Margin = new Thickness(2, 0, 0, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
    };

    private static BitmapImage? DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }
        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
