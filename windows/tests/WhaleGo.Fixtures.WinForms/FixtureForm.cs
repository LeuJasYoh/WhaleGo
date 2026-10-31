using System.Globalization;

namespace WhaleGo.Fixtures.WinForms;

/// <summary>
/// Main fixture window. Layout/automation ids are part of the test contract; see README.md.
/// </summary>
internal sealed class FixtureForm : Form
{
    private const string BaseTitle = "WhaleGo Fixture";

    private readonly FixtureOptions _options;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer? _autoCloseTimer;

    private readonly Button _incrementButton = new();
    private readonly Label _counterLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _inputBox = new();
    private readonly Button _setTextButton = new();
    private readonly TextBox _secretBox = new();
    private readonly CheckBox _toggleCheck = new();
    private readonly ListBox _itemsList = new();
    private readonly ComboBox _choiceCombo = new();
    private readonly Panel _clickPanel = new();
    private readonly Button _clickTargetButton = new();
    private readonly Label _clickCountLabel = new();
    private readonly Button _disabledButton = new();

    private int _counter;
    private int _panelClicks;
    private int _targetClicks;
    private bool _dpiScaled;

    public FixtureForm(FixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        SuspendLayout();
        ConfigureForm();
        BuildLayout();
        ResumeLayout(performLayout: true);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();

        if (options.AutoCloseMs is int autoCloseMs)
        {
            _autoCloseTimer = new System.Windows.Forms.Timer { Interval = Math.Max(autoCloseMs, 1) };
            _autoCloseTimer.Tick += OnAutoCloseTick;
            _autoCloseTimer.Start();
        }

        RefreshStatus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Defer one message-loop turn: the window must be fully created/positioned before
        // DeviceDpi and the working area are final (otherwise the scale step is a no-op).
        BeginInvoke(new MethodInvoker(() =>
        {
            ApplyDpiScaleIfNeeded();
            RefreshStatus();
            WriteReadyFile();
        }));
    }

    /// <summary>
    /// Scale the nominal 96-DPI design size (900x650 client, 240x140 click panel) to the current
    /// monitor DPI. The process itself is PerMonitorV2 aware (app.manifest + SetHighDpiMode), so the
    /// window is rendered crisply; larger controls/fonts reflow through the layout panels.
    /// </summary>
    private void ApplyDpiScaleIfNeeded()
    {
        if (_dpiScaled)
        {
            return;
        }

        _dpiScaled = true;
        var dpi = DeviceDpi;
        if (dpi <= 0 || dpi == 96)
        {
            return;
        }

        var scale = dpi / 96.0;
        var workingArea = Screen.FromControl(this).WorkingArea;
        var width = (int)Math.Round(ClientSize.Width * scale, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(ClientSize.Height * scale, MidpointRounding.AwayFromZero);
        width = Math.Min(width, Math.Max(workingArea.Width - 80, 640));
        height = Math.Min(height, Math.Max(workingArea.Height - 80, 480));
        ClientSize = new Size(width, height);
        _clickPanel.Size = new Size(
            (int)Math.Round(240 * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(140 * scale, MidpointRounding.AwayFromZero));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _autoCloseTimer?.Stop();
        _autoCloseTimer?.Dispose();
        base.OnFormClosed(e);
    }

    private void ConfigureForm()
    {
        Name = "frmFixture";
        Text = _options.TitleSuffix is { Length: > 0 } suffix
            ? string.Concat(BaseTitle, " - ", suffix)
            : BaseTitle;
        AccessibleName = Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 650);
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        ShowIcon = false;
        ShowInTaskbar = true;
        MaximizeBox = true;
        MinimizeBox = true;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Name = "tlpRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            AutoScroll = true,
            Padding = new Padding(12),
            Font = Font,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 4; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        root.Controls.Add(BuildCounterGroup(), 0, 0);
        root.Controls.Add(BuildInputGroup(), 0, 1);
        root.Controls.Add(BuildListsGroup(), 0, 2);
        root.Controls.Add(BuildClickGroup(), 0, 3);
        Controls.Add(root);
    }

    private GroupBox BuildCounterGroup()
    {
        var group = new GroupBox
        {
            Name = "grpCounter",
            Text = "Counter",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };

        var grid = new TableLayoutPanel
        {
            Name = "tlpCounter",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _incrementButton.Name = "btnIncrement";
        _incrementButton.Text = "Increment";
        _incrementButton.AutoSize = true;
        _incrementButton.MinimumSize = new Size(140, 40);
        _incrementButton.Margin = new Padding(0, 4, 12, 4);
        _incrementButton.Click += OnIncrementClick;

        _counterLabel.Name = "lblCounter";
        _counterLabel.Text = "0";
        _counterLabel.AutoSize = true;
        _counterLabel.Font = new Font("Segoe UI", 20F, FontStyle.Bold, GraphicsUnit.Point);
        _counterLabel.Margin = new Padding(0, 0, 0, 0);
        _counterLabel.TextAlign = ContentAlignment.MiddleLeft;

        _statusLabel.Name = "lblStatus";
        _statusLabel.Text = "counter=0;check=off;text=;clicks=0";
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        _statusLabel.Margin = new Padding(0, 8, 0, 0);

        grid.Controls.Add(_incrementButton, 0, 0);
        grid.Controls.Add(_counterLabel, 1, 0);
        grid.Controls.Add(_statusLabel, 0, 1);
        grid.SetColumnSpan(_statusLabel, 2);
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildInputGroup()
    {
        var group = new GroupBox
        {
            Name = "grpInput",
            Text = "Input",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };

        var grid = new TableLayoutPanel
        {
            Name = "tlpInput",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _inputBox.Name = "txtInput";
        _inputBox.AccessibleName = "Input";
        _inputBox.Margin = new Padding(0, 4, 12, 4);
        _inputBox.Dock = DockStyle.Fill;

        _setTextButton.Name = "btnSetText";
        _setTextButton.Text = "Set";
        _setTextButton.AutoSize = true;
        _setTextButton.MinimumSize = new Size(120, 34);
        _setTextButton.Margin = new Padding(0, 4, 0, 4);
        _setTextButton.Click += OnSetTextClick;

        _secretBox.Name = "txtSecret";
        _secretBox.Text = "secret123";
        _secretBox.UseSystemPasswordChar = true;
        _secretBox.Margin = new Padding(0, 4, 12, 4);

        _toggleCheck.Name = "chkToggle";
        _toggleCheck.Text = "Toggle";
        _toggleCheck.AutoSize = true;
        _toggleCheck.Margin = new Padding(0, 8, 0, 4);

        grid.Controls.Add(_inputBox, 0, 0);
        grid.Controls.Add(_setTextButton, 1, 0);
        grid.Controls.Add(_secretBox, 0, 1);
        grid.Controls.Add(_toggleCheck, 1, 1);
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildListsGroup()
    {
        var group = new GroupBox
        {
            Name = "grpLists",
            Text = "Lists",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };

        var grid = new TableLayoutPanel
        {
            Name = "tlpLists",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _itemsList.Name = "lstItems";
        _itemsList.IntegralHeight = false;
        _itemsList.Dock = DockStyle.Fill;
        _itemsList.Height = 110;
        _itemsList.Margin = new Padding(0, 4, 12, 4);
        _itemsList.Items.AddRange(["A", "B", "C"]);

        _choiceCombo.Name = "cboChoice";
        _choiceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _choiceCombo.Dock = DockStyle.Fill;
        _choiceCombo.Margin = new Padding(0, 4, 0, 4);
        _choiceCombo.Items.AddRange(["X", "Y", "Z"]);
        _choiceCombo.SelectedIndex = 0;

        grid.Controls.Add(_itemsList, 0, 0);
        grid.Controls.Add(_choiceCombo, 1, 0);
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildClickGroup()
    {
        var group = new GroupBox
        {
            Name = "grpClick",
            Text = "Click targets",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };

        var grid = new TableLayoutPanel
        {
            Name = "tlpClick",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _clickPanel.Name = "pnlClick";
        _clickPanel.BorderStyle = BorderStyle.FixedSingle;
        _clickPanel.BackColor = Color.FromArgb(235, 244, 255);
        _clickPanel.Size = new Size(240, 140);
        _clickPanel.Margin = new Padding(0, 4, 16, 4);
        // The panel deliberately has NO child controls: tests click its own centre (UIA Invoke or
        // coordinate path) and the panel Click handler must receive the event.
        _clickPanel.Click += OnClickPanelClick;

        var side = new TableLayoutPanel
        {
            Name = "tlpClickSide",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Dock = DockStyle.Fill,
        };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _clickTargetButton.Name = "btnClickTarget";
        _clickTargetButton.Text = "ClickTarget";
        _clickTargetButton.AutoSize = true;
        _clickTargetButton.MinimumSize = new Size(160, 40);
        _clickTargetButton.Margin = new Padding(0, 4, 0, 8);
        _clickTargetButton.Click += OnClickTargetClick;

        _clickCountLabel.Name = "lblClickCount";
        _clickCountLabel.Text = "panel=0;target=0";
        _clickCountLabel.AutoSize = true;
        _clickCountLabel.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        _clickCountLabel.Margin = new Padding(0, 0, 0, 8);

        _disabledButton.Name = "btnDisabled";
        _disabledButton.Text = "Disabled";
        _disabledButton.AutoSize = true;
        _disabledButton.MinimumSize = new Size(160, 40);
        _disabledButton.Enabled = false;
        _disabledButton.Margin = new Padding(0, 0, 0, 4);

        side.Controls.Add(_clickTargetButton, 0, 0);
        side.Controls.Add(_clickCountLabel, 0, 1);
        side.Controls.Add(_disabledButton, 0, 2);
        side.Controls.Add(new Label { Name = "lblClickSpacer", Text = string.Empty, AutoSize = true }, 0, 3);

        grid.Controls.Add(_clickPanel, 0, 0);
        grid.Controls.Add(side, 1, 0);
        group.Controls.Add(grid);
        return group;
    }

    private void OnStatusTick(object? sender, EventArgs e) => RefreshStatus();

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        _autoCloseTimer?.Stop();
        Close();
    }

    private void OnIncrementClick(object? sender, EventArgs e)
    {
        _counter++;
        RefreshStatus();
    }

    private void OnSetTextClick(object? sender, EventArgs e)
    {
        _inputBox.Text = "set-by-button";
        RefreshStatus();
    }

    private void OnClickPanelClick(object? sender, EventArgs e)
    {
        _panelClicks++;
        RefreshStatus();
        AppendClickLog("pnlClick");
    }

    private void OnClickTargetClick(object? sender, EventArgs e)
    {
        _targetClicks++;
        RefreshStatus();
        AppendClickLog("btnClickTarget");
    }

    private void RefreshStatus()
    {
        var counterText = _counter.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(_counterLabel.Text, counterText, StringComparison.Ordinal))
        {
            _counterLabel.Text = counterText;
        }

        var checkText = _toggleCheck.Checked ? "on" : "off";
        var status = string.Create(
            CultureInfo.InvariantCulture,
            $"counter={_counter};check={checkText};text={_inputBox.Text};clicks={_panelClicks + _targetClicks}");
        if (_options.TitleSuffix is { Length: > 0 } suffix)
        {
            status = string.Concat(status, ";suffix=", suffix);
        }

        if (!string.Equals(_statusLabel.Text, status, StringComparison.Ordinal))
        {
            _statusLabel.Text = status;
        }

        var clickCount = string.Create(
            CultureInfo.InvariantCulture,
            $"panel={_panelClicks};target={_targetClicks}");
        if (!string.Equals(_clickCountLabel.Text, clickCount, StringComparison.Ordinal))
        {
            _clickCountLabel.Text = clickCount;
        }
    }

    private void AppendClickLog(string target)
    {
        if (string.IsNullOrWhiteSpace(_options.ClickLog))
        {
            return;
        }

        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} target={target} panel={_panelClicks} clickTarget={_targetClicks}{Environment.NewLine}");
            File.AppendAllText(_options.ClickLog!, line);
        }
        catch (IOException)
        {
            // Click logging must never break the fixture; tests may still assert on lblClickCount.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteReadyFile()
    {
        if (string.IsNullOrWhiteSpace(_options.ReadyFile))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(_options.ReadyFile!);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var content = string.Create(
                CultureInfo.InvariantCulture,
                $"ready=true{Environment.NewLine}pid={Environment.ProcessId}{Environment.NewLine}title={Text}{Environment.NewLine}dpi={DeviceDpi}{Environment.NewLine}client={ClientSize.Width}x{ClientSize.Height}{Environment.NewLine}hwnd=0x{Handle.ToInt64():X}{Environment.NewLine}");
            File.WriteAllText(fullPath, content);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"WhaleGo.Fixtures.WinForms: could not write ready file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"WhaleGo.Fixtures.WinForms: could not write ready file: {ex.Message}");
        }
    }
}