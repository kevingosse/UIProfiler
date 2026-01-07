using System.Diagnostics;
using System.Runtime.InteropServices;
using Silhouette;

namespace UiProfiler;

internal class DllMain
{
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static unsafe HResult DllGetClassObject(Guid* rclsid, Guid* riid, nint* ppv)
    {
        if (!string.Equals(Process.GetCurrentProcess().ProcessName, "devenv", StringComparison.OrdinalIgnoreCase))
        {
            return HResult.CORPROF_E_PROFILER_CANCEL_ACTIVATION;
        }

        if (*rclsid != new Guid("0A96F866-D763-4099-8E4E-ED1801BE9FBD"))
        {
            Logger.Log($"DllGetClassObject: Invalid CLSID {*rclsid}");
            return HResult.CORPROF_E_PROFILER_CANCEL_ACTIVATION;
        }

        // Disable profiling for any child processes
        Environment.SetEnvironmentVariable("COR_ENABLE_PROFILING", "0");

        *ppv = ClassFactory.For(new CorProfilerCallback());

        Logger.Log("Profiler initialized");

        return HResult.S_OK;
    }
}
