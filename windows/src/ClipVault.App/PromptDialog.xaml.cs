using System.Windows;

namespace ClipVault.App;

/// <summary>
/// A small modal dialog with one or two text fields, standing in for the web
/// UI's native <c>prompt()</c> calls (label/secret entry, the vault's "move
/// into vault" label). Values are shown and typed in plain text, same as
/// <c>prompt()</c> itself - it has no password masking.
/// </summary>
public partial class PromptDialog : Window
{
    public string Field1Value => Field1Box.Text;
    public string Field2Value => Field2Box.Text;

    private PromptDialog()
    {
        InitializeComponent();
    }

    /// <summary>One text field. Returns the entered value, or null if cancelled or left blank.</summary>
    public static string? ShowSingle(Window owner, string message, string field1Label, string defaultValue = "")
    {
        var dialog = Build(owner, message, field1Label, defaultValue, null, null);
        return dialog.ShowDialog() == true && dialog.Field1Value.Trim().Length > 0
            ? dialog.Field1Value.Trim()
            : null;
    }

    /// <summary>Two text fields. Returns both values, or null if cancelled or either is left blank.</summary>
    public static (string Field1, string Field2)? ShowDouble(
        Window owner, string message, string field1Label, string field2Label)
    {
        var dialog = Build(owner, message, field1Label, "", field2Label, "");
        if (dialog.ShowDialog() != true)
        {
            return null;
        }
        var (v1, v2) = (dialog.Field1Value.Trim(), dialog.Field2Value);
        return v1.Length > 0 && v2.Length > 0 ? (v1, v2) : null;
    }

    private static PromptDialog Build(
        Window owner, string message, string field1Label, string field1Default, string? field2Label, string? field2Default)
    {
        var dialog = new PromptDialog { Owner = owner };
        dialog.MessageText.Text = message;
        dialog.Field1Label.Text = field1Label;
        dialog.Field1Box.Text = field1Default;

        if (field2Label is null)
        {
            dialog.Field2Panel.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.Field2Label.Text = field2Label;
            dialog.Field2Box.Text = field2Default ?? "";
        }

        dialog.Loaded += (_, _) =>
        {
            dialog.Field1Box.Focus();
            dialog.Field1Box.SelectAll();
        };
        return dialog;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
