using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Silhouette;

namespace UiProfiler;

internal class CorProfilerCallback : CorProfilerCallback4Base
{
    private static string? _overlayPath;

    private readonly BlockingCollection<string> _messages = new();
    private readonly ManualResetEventSlim _responsiveMutex = new(false);

    private uint _mainThreadId;
    private long _pausesCount;

    protected override HResult Initialize(int iCorProfilerInfoVersion)
    {
        var modulePath = NativeMethods.GetModulePath();

        if (modulePath == null)
        {
            return HResult.E_FAIL;
        }

        _overlayPath = Path.Combine(Path.GetDirectoryName(modulePath)!, "UiProfiler.Overlay.exe");

        SuperluminalPerf.Initialize();

        _mainThreadId = NativeMethods.GetCurrentThreadId();

        new Thread(SenderThread)
        {
            IsBackground = true,
            Name = "UI Profiler Thread"
        }.Start();

        new Thread(InputThread)
        {
            IsBackground = true,
            Name = "UI Profiler Monitor"
        }.Start();

        new Thread(MonitoringThread)
        {
            IsBackground = true,
            Name = "UI Responsiveness Monitor"
        }.Start();

        return HResult.S_OK;
    }

    private void SetHook(int threadId)
    {
        NativeMethods.SetWindowsHookEx(NativeMethods.HookType.WH_MOUSE, HookProc, 0, threadId);

        IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                _responsiveMutex.Set();
            }

            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }
    }

    private void MonitoringThread()
    {
        SuperluminalPerf.SetCurrentThreadName("UI Responsiveness Monitor");
        var color = new SuperluminalPerf.ProfilerColor(255, 0, 0);

        bool isResponsive = true;

        var stopwatch = Stopwatch.StartNew();

        SuperluminalPerf.EventMarker eventMarker = default;

        while (true)
        {
            if (_responsiveMutex.Wait(20))
            {
                _responsiveMutex.Reset();

                if (!isResponsive)
                {
                    isResponsive = true;
                    eventMarker.Dispose();
                    stopwatch.Stop();
                    _messages.Add($"true|{stopwatch.ElapsedMilliseconds}");
                }
            }
            else
            {
                if (isResponsive)
                {
                    if (!IsCursorOverProcessWindow())
                    {
                        continue;
                    }

                    isResponsive = false;

                    var index = Interlocked.Increment(ref _pausesCount);
                    eventMarker = SuperluminalPerf.BeginEvent("UI freeze", $"Freeze {index}", color);
                    stopwatch.Restart();
                    _messages.Add("false");

                    // Now wait indefinitely
                    _responsiveMutex.Wait();
                }
            }
        }
    }

    private void InputThread()
    {
        SetHook((int)_mainThreadId);

        int mouseDelta = 2;

        try
        {
            var inputs = new NativeMethods.INPUT[1];

            while (true)
            {
                Thread.Sleep(10);

                inputs[0] = new()
                {
                    mi = new()
                    {
                        dx = mouseDelta,
                        dy = 0,
                        dwFlags = 0x1 /* MOUSEEVENTF_MOVE */
                    },
                    type = 0x0 /* INPUT_MOUSE */
                };

                mouseDelta = -mouseDelta;

                var res = NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

                if (res != 1)
                {
                    var error = Marshal.GetLastWin32Error();
                    Logger.Log($"SendInput returned {res} - {error:x2}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"InputThread failed: {ex}");
        }
    }

    private void SenderThread()
    {
        try
        {
            var pipeName = $"UIProfiler-{Guid.NewGuid()}";

            var startInfo = new ProcessStartInfo(_overlayPath!)
            {
                UseShellExecute = false,
                Arguments = $"{Environment.ProcessId} {pipeName}"
            };

            Process.Start(startInfo);

            using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipeClient.Connect();

            using var writer = new StreamWriter(pipeClient);
            writer.AutoFlush = true;

            foreach (var message in _messages.GetConsumingEnumerable())
            {
                writer.WriteLine(message);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SenderThread failed: {ex}");
        }
    }

    private static bool IsCursorOverProcessWindow()
    {
        if (!NativeMethods.GetCursorPos(out var cursorPosition))
        {
            return false;
        }

        var isOverWindow = false;

        NativeMethods.EnumWindows(
            (hWnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

                if (pid != Environment.ProcessId || !NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                {
                    return true;
                }

                if (cursorPosition.X < rect.Left || cursorPosition.X >= rect.Right ||
                    cursorPosition.Y < rect.Top || cursorPosition.Y >= rect.Bottom)
                {
                    return true;
                }

                isOverWindow = true;
                return false;
            },
            IntPtr.Zero);

        return isOverWindow;
    }
}