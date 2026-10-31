using System.Drawing;
using System.Windows.Forms;

namespace WhaleGo.Platform.Tests.TestInfrastructure;

/// <summary>In-process WinForms control fixture with known UIA ids/texts for deterministic observation tests.</summary>
internal static class TestFixtureForm
{
    public const string BaseTitle = "WhaleGo Platform Test Fixture";

    public const string IncrementButtonText = "Increment";
    public const string ToggleCheckBoxText = "Toggle option";
    public const string DisabledButtonText = "Disabled action";
    public const string InputAccessibleName = "Input field";
    public const string SecretAccessibleName = "Secret field";
    public const string LongTextAccessibleName = "Long text field";
    public const string ListAccessibleName = "Items list";
    public const string ComboAccessibleName = "Choice box";
    public const string InputInitialText = "hello";
    public const string SecretPlaintext = "s3cr3t-value";
    public const string LongTextContent = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    public static string BuildTitle(string? suffix) =>
        string.IsNullOrEmpty(suffix) ? BaseTitle : BaseTitle + " - " + suffix;

    public static void Configure(Form form, string? suffix = null, Action<Form>? customize = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.Text = BuildTitle(suffix);

        var counter = new Label { Name = "lblCounter", Text = "counter=0", Left = 160, Top = 18, Width = 200, AutoSize = true };

        var increment = new Button
        {
            Name = "btnIncrement",
            Text = IncrementButtonText,
            AccessibleName = IncrementButtonText,
            Left = 12,
            Top = 12,
            Width = 130,
            Height = 32,
        };
        var clicks = 0;
        increment.Click += (_, _) =>
        {
            clicks++;
            counter.Text = "counter=" + clicks;
        };

        var input = new TextBox
        {
            Name = "txtInput",
            Text = InputInitialText,
            AccessibleName = InputAccessibleName,
            Left = 12,
            Top = 56,
            Width = 250,
        };

        var secret = new TextBox
        {
            Name = "txtSecret",
            Text = SecretPlaintext,
            UseSystemPasswordChar = true,
            AccessibleName = SecretAccessibleName,
            Left = 12,
            Top = 92,
            Width = 250,
        };

        var toggle = new CheckBox
        {
            Name = "chkToggle",
            Text = ToggleCheckBoxText,
            AccessibleName = ToggleCheckBoxText,
            Left = 12,
            Top = 126,
            Width = 220,
            AutoSize = true,
        };

        var list = new ListBox
        {
            Name = "lstItems",
            AccessibleName = ListAccessibleName,
            Left = 12,
            Top = 160,
            Width = 250,
            Height = 120,
        };
        list.Items.AddRange(["Alpha", "Beta", "Gamma"]);

        var combo = new ComboBox
        {
            Name = "cboChoice",
            AccessibleName = ComboAccessibleName,
            Left = 290,
            Top = 160,
            Width = 180,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        combo.Items.AddRange(["Red", "Green", "Blue"]);
        combo.SelectedIndex = 0;

        var longText = new TextBox
        {
            Name = "txtLong",
            AccessibleName = LongTextAccessibleName,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Left = 290,
            Top = 200,
            Width = 240,
            Height = 140,
            Text = new string('x', 300),
        };

        var disabled = new Button
        {
            Name = "btnDisabled",
            Text = DisabledButtonText,
            AccessibleName = DisabledButtonText,
            Left = 290,
            Top = 12,
            Width = 150,
            Height = 32,
            Enabled = false,
        };

        form.Controls.AddRange([counter, increment, input, secret, toggle, list, combo, longText, disabled]);
        customize?.Invoke(form);
    }
}
