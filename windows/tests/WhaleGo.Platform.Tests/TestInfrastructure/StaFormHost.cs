using System.Drawing;
using System.Windows.Forms;

namespace WhaleGo.Platform.Tests.TestInfrastructure;

/// <summary>
/// Deterministic in-process WinForms window on a dedicated STA thread with its own message loop.
/// The observable facts (title/handle/client origin) are captured once on Shown so tests never read WinForms
/// properties cross-thread.
/// </summary>
internal sealed class StaFormHost : IDisposable
{
    private static readonly object VisualStylesGate = new();
    private static bool _visualStylesReady;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private Form? _form;
    private Exception? _error;

    private StaFormHost()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WhaleGo.Platform.Tests.FormHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public string Title { get; private set; } = string.Empty;

    public IntPtr Handle { get; private set; }

    public Size ClientSize { get; private set; }

    public Point ClientOriginScreen { get; private set; }

    public static StaFormHost Start(Action<Form> build, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        var host = new StaFormHost();
        host._thread.Start(build);
        if (!host._shown.Wait(timeout ?? TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("Test form did not become visible within the timeout.");
        }

        if (host._error is not null)
        {
            throw new InvalidOperationException("Test form failed to start: " + host._error.Message, host._error);
        }

        return host;
    }

    /// <summary>Run an action on the form's UI thread (used to mutate the window, e.g. resize).</summary>
    public void Invoke(Action<Form> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var form = _form ?? throw new InvalidOperationException("Test form is not created.");
        if (form.InvokeRequired)
        {
            form.Invoke(new Action(() => action(form)));
        }
        else
        {
            action(form);
        }
    }

    public void ResizeClient(int width, int height) => Invoke(form => form.ClientSize = new Size(width, height));

    public string GetControlText(string name)
    {
        var form = _form ?? throw new InvalidOperationException("Test form is not created.");
        string? text = null;
        form.Invoke(new Action(() =>
        {
            var controls = form.Controls.Find(name, searchAllChildren: true);
            text = controls.Length > 0 ? controls[0].Text : null;
        }));
        return text ?? string.Empty;
    }

    public void Dispose()
    {
        try
        {
            var form = _form;
            if (form is { IsHandleCreated: true, IsDisposed: false })
            {
                form.BeginInvoke(new Action(() => form.Close()));
            }
        }
        catch
        {
            // form may already be gone
        }

        _closed.Wait(TimeSpan.FromSeconds(5));
        _shown.Dispose();
        _closed.Dispose();
    }

    private void Run(object? state)
    {
        var build = (Action<Form>)state!;
        try
        {
            EnsureVisualStyles();
            using var form = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(140, 140),
                ClientSize = new Size(560, 440),
                ShowInTaskbar = true,
            };
            build(form);
            _form = form;
            form.Shown += (_, _) =>
            {
                try
                {
                    Title = form.Text;
                    Handle = form.Handle;
                    ClientSize = form.ClientSize;
                    ClientOriginScreen = form.PointToScreen(Point.Empty);
                }
                finally
                {
                    _shown.Set();
                }
            };
            Application.Run(form);
        }
        catch (Exception exception)
        {
            _error = exception;
            _shown.Set();
        }
        finally
        {
            _closed.Set();
        }
    }

    private static void EnsureVisualStyles()
    {
        lock (VisualStylesGate)
        {
            if (_visualStylesReady)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _visualStylesReady = true;
        }
    }
}
