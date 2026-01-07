using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Interop;

namespace UiProfiler.Overlay;

public partial class App
{
    private Process? _parentProcess;
    private readonly CancellationTokenSource _cts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length < 2)
        {
            MessageBox.Show("Usage: <pid> <pipe_name>", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var pid = int.Parse(e.Args[0]);
        var pipeName = e.Args[1];

        _parentProcess = Process.GetProcessById(pid);
        _parentProcess.Exited += (_, _) => Dispatcher.Invoke(Shutdown);
        _parentProcess.EnableRaisingEvents = true;

        var overlay = LoadOverlay();

        Task.Run(async () =>
        {
            var window = FindWindowForProcess(pid, IntPtr.Zero);
            NativeMethods.GetWindowRect(window, out NativeMethods.RECT prevRect);
            await Dispatcher.BeginInvoke(() => overlay.Show(window));

            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100);
                var newWindow = FindWindowForProcess(pid, window);

                NativeMethods.GetWindowRect(newWindow, out var newRect);

                bool windowChanged = newWindow != window;
                bool rectChanged = newRect.Left != prevRect.Left || newRect.Top != prevRect.Top || newRect.Right != prevRect.Right || newRect.Bottom != prevRect.Bottom;

                if (windowChanged || rectChanged)
                {
                    await Dispatcher.BeginInvoke(() => overlay.AdjustSize(newWindow));
                    window = newWindow;
                    prevRect = newRect;
                }
            }
        });

        new Thread(() => Listener(pipeName, overlay))
        {
            IsBackground = true,
            Name = "Listener Thread"
        }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ = _cts.CancelAsync();
        base.OnExit(e);
    }

    private static IntPtr FindWindowForProcess(int processId, IntPtr previousWindow)
    {
        var foundWindow = IntPtr.Zero;
        var firstWindow = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var windowProcessId);

            if (windowProcessId != processId || !NativeMethods.IsWindowVisible(hWnd))
            {
                return true; // continue
            }

            if (hWnd == previousWindow)
            {
                foundWindow = hWnd;
                return false; // stop
            }

            if (firstWindow == IntPtr.Zero)
            {
                firstWindow = hWnd;
            }

            return true; // continue
        },
        IntPtr.Zero);

        return foundWindow != IntPtr.Zero ? foundWindow : firstWindow;
    }

    private void Listener(string pipeName, OverlayWindow overlay)
    {
        try
        {
            using var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut);
            pipeServer.WaitForConnection();

            using var reader = new StreamReader(pipeServer);

            while (true)
            {
                var message = reader.ReadLine();

                if (message == null)
                {
                    break;
                }

                var values = message.Split('|');

                if (values[0] == "true")
                {
                    Dispatcher.BeginInvoke(() => overlay.UpdateResponsiveness(true, long.Parse(values[1])));
                }
                else if (values[0] == "false")
                {
                    Dispatcher.BeginInvoke(() => overlay.UpdateResponsiveness(false, 0));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error in pipe listener: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static OverlayWindow LoadOverlay()
    {
        var overlay = new OverlayWindow
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true
        };

        overlay.SourceInitialized += (s, e) =>
        {
            var handle = new WindowInteropHelper(overlay).Handle;
            var extendedStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);

            // Add WS_EX_TRANSPARENT style to allow clicks to pass through
            _ = NativeMethods.SetWindowLong(
                handle,
                NativeMethods.GWL_EXSTYLE,
                extendedStyle | NativeMethods.WS_EX_TRANSPARENT);
        };

        return overlay;
    }
}
