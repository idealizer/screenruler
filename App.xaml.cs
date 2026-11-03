using System.IO;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenRuler;

public partial class App : Application
{
    private const string MutexName = @"Local\Idealizer.ScreenRuler";
    private const string ShowEventName = @"Local\Idealizer.ScreenRuler.Show";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _stopEvent;
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _icon;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        if (!AcquireInstance())
        {
            Shutdown();
            return;
        }

        try
        {
            var settings = AppSettings.Load();
            _window = new MainWindow(settings);
            _icon = CreateIcon();
            _window.UseIcon(_icon);
            CreateTray(_icon);
            StartShowListener();
            _window.Show();
        }
        catch (Exception ex)
        {
            Log(ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stopEvent?.Set();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _icon?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released.
            }
        }

        _mutex?.Dispose();
        _showEvent?.Dispose();
        _stopEvent?.Dispose();
        base.OnExit(e);
    }

    private bool AcquireInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool created);
            if (created)
            {
                _ownsMutex = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            try
            {
                _mutex = Mutex.OpenExisting(MutexName);
                _ownsMutex = true;
            }
            catch
            {
                _ownsMutex = true;
            }

            return true;
        }

        SignalExisting();
        return false;
    }

    private static void SignalExisting()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void StartShowListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        var thread = new Thread(() =>
        {
            WaitHandle[] handles = [_showEvent, _stopEvent];
            while (WaitHandle.WaitAny(handles) == 0)
                Dispatcher.BeginInvoke(() => _window?.ShowAndActivate());
        })
        {
            IsBackground = true,
            Name = "ScreenRuler.Show"
        };
        thread.Start();
    }

    private void CreateTray(Drawing.Icon icon)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show (Ctrl+Alt+R)", null, (_, _) => Ui(() => _window?.ShowAndActivate()));
        menu.Items.Add("Horizontal", null, (_, _) => Ui(() => _window?.SetMode(MeasureMode.Horizontal)));
        menu.Items.Add("Vertical", null, (_, _) => Ui(() => _window?.SetMode(MeasureMode.Vertical)));
        menu.Items.Add("Area", null, (_, _) => Ui(() => _window?.SetMode(MeasureMode.Area)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Ui(ExitApp));

        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Screen Ruler",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Ui(() => _window?.ShowAndActivate());
        };
    }

    private void ExitApp()
    {
        _stopEvent?.Set();
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        Shutdown();
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void Log(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenRuler");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"{DateTime.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not throw.
        }
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);
            using var path = RoundedRect(1, 8, 30, 16, 4);
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(235, 15, 23, 42));
            graphics.FillPath(fill, path);
            using var tick = new Drawing.Pen(Drawing.Color.FromArgb(255, 45, 212, 191), 1.6f);
            for (int x = 6; x <= 27; x += 4)
            {
                float length = (x - 6) % 12 == 0 ? 8f : 4.5f;
                graphics.DrawLine(tick, x, 9, x, 9 + length);
            }

            using var origin = new Drawing.Pen(Drawing.Color.FromArgb(255, 251, 191, 36), 2f);
            graphics.DrawLine(origin, 3, 9, 3, 22);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var icon = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRect(int x, int y, int width, int height, int radius)
    {
        int diameter = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
