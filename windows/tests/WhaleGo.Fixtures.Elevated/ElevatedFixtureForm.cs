using System.Globalization;

namespace WhaleGo.Fixtures.Elevated;

/// <summary>Minimal high-integrity window: used to exercise the executor's UIPI pre-check path.</summary>
internal sealed class ElevatedFixtureForm : Form
{
    private const string BaseTitle = "WhaleGo Fixture Elevated";

    private readonly FixtureOptions _options;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer? _autoCloseTimer;
    private readonly Button _incrementButton = new();
    private readonly Label _counterLabel = new();
    private readonly Label _statusLabel = new();

    private int _counter;

    public ElevatedFixtureForm(FixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        SuspendLayout();
        Name = "frmElevated";
        Text = options.TitleSuffix is { Length: > 0 } suffix
            ? string.Concat(BaseTitle, " - ", suffix)
            : BaseTitle;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 360);
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        ShowInTaskbar = true;

        var grid = new TableLayoutPanel
        {
            Name = "tlpElevated",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(16),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _incrementButton.Name = "btnIncrement";
        _incrementButton.Text = "Increment";
        _incrementButton.AutoSize = true;
        _incrementButton.MinimumSize = new Size(160, 44);
        _incrementButton.Margin = new Padding(0, 0, 16, 8);
        _incrementButton.Click += OnIncrementClick;

        _counterLabel.Name = "lblCounter";
        _counterLabel.Text = "0";
        _counterLabel.AutoSize = true;
        _counterLabel.Font = new Font("Segoe UI", 20F, FontStyle.Bold, GraphicsUnit.Point);
        _counterLabel.Margin = new Padding(0, 0, 0, 0);

        _statusLabel.Name = "lblStatus";
        _statusLabel.Text = "counter=0;elevated=true";
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        _statusLabel.Margin = new Padding(0, 8, 0, 0);

        grid.Controls.Add(_incrementButton, 0, 0);
        grid.Controls.Add(_counterLabel, 1, 0);
        grid.Controls.Add(_statusLabel, 0, 1);
        grid.SetColumnSpan(_statusLabel, 2);
        Controls.Add(grid);
        ResumeLayout(performLayout: true);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        if (options.AutoCloseMs is int autoCloseMs)
        {
            _autoCloseTimer = new System.Windows.Forms.Timer { Interval = Math.Max(autoCloseMs, 1) };
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer?.Stop();
                Close();
            };
            _autoCloseTimer.Start();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshStatus();
        WriteReadyFile();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _autoCloseTimer?.Stop();
        _autoCloseTimer?.Dispose();
        base.OnFormClosed(e);
    }

    private void OnIncrementClick(object? sender, EventArgs e)
    {
        _counter++;
        RefreshStatus();
        AppendClickLog();
    }

    private void RefreshStatus()
    {
        var counterText = _counter.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(_counterLabel.Text, counterText, StringComparison.Ordinal))
        {
            _counterLabel.Text = counterText;
        }

        var status = string.Create(CultureInfo.InvariantCulture, $"counter={_counter};elevated=true");
        if (!string.Equals(_statusLabel.Text, status, StringComparison.Ordinal))
        {
            _statusLabel.Text = status;
        }
    }

    private void AppendClickLog()
    {
        if (string.IsNullOrWhiteSpace(_options.ClickLog))
        {
            return;
        }

        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} target=btnIncrement counter={_counter}{Environment.NewLine}");
            File.AppendAllText(_options.ClickLog!, line);
        }
        catch (IOException)
        {
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
                $"ready=true{Environment.NewLine}pid={Environment.ProcessId}{Environment.NewLine}title={Text}{Environment.NewLine}dpi={DeviceDpi}{Environment.NewLine}elevated=true{Environment.NewLine}");
            File.WriteAllText(fullPath, content);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"WhaleGo.Fixtures.Elevated: could not write ready file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"WhaleGo.Fixtures.Elevated: could not write ready file: {ex.Message}");
        }
    }
}
