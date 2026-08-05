using System.Windows.Forms;
using HeadPointerMvp.Services;

namespace HeadPointerMvp;

internal static class Program
{
    // Held for the process's entire lifetime (released automatically when it exits) -- must stay a
    // static field so it's never garbage collected, which would release the lock early.
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main()
    {
        // Nothing enforced this before: two instances could run at once, each opening its own UDP
        // pose-receiver socket, hotkey registrations, and microphone capture -- colliding in ways that
        // looked like "things randomly stopped working" (one instance silently losing the port/hotkey
        // race to the other, or two voice engines listening on the same mic simultaneously).
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "HeadPointerMvp-SingleInstance-9F2B6E7C-4A3D-4E9F-8C1B-6D4A2E9F7B3C", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "HeadPointer is already running. Check the system tray.",
                "HeadPointer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatalError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowFatalError(ex);
        };
        Application.Run(new HeadPointerApplicationContext());
    }

    private static void ShowFatalError(Exception ex)
    {
        MessageBox.Show(
            $"HeadPointer stopped unexpectedly.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "HeadPointer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

internal sealed class HeadPointerApplicationContext : ApplicationContext
{
    private AppHost? _host;

    public HeadPointerApplicationContext()
    {
        var startupTimer = new System.Windows.Forms.Timer { Interval = 1 };
        startupTimer.Tick += async (_, _) =>
        {
            startupTimer.Stop();
            startupTimer.Dispose();

            try
            {
                _host = await AppHost.StartAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"HeadPointer could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "HeadPointer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                ExitThread();
            }
        };
        startupTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _host is not null)
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
