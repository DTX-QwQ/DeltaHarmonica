using System.Runtime.InteropServices;
using System.Text;

namespace DeltaHarmonica.App.Services;

public sealed record WindowTarget(nint Handle, uint ProcessId, string Title);

/// <summary>Limits playback to the exact external window captured at the end of the countdown.</summary>
public sealed class ForegroundWindowService
{
    public WindowTarget? CaptureTarget()
    {
        var target = GetForegroundTarget();
        return target is not null && target.ProcessId != (uint)Environment.ProcessId ? target : null;
    }

    public WindowTarget? GetForegroundTarget()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) return null;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;

        var title = new StringBuilder(Math.Clamp(GetWindowTextLength(handle) + 1, 1, 32768));
        GetWindowText(handle, title, title.Capacity);
        return new WindowTarget(handle, processId, title.ToString());
    }

    public bool IsForeground(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Handle == 0 || target.ProcessId == (uint)Environment.ProcessId)
            return false;
        var handle = GetForegroundWindow();
        if (handle != target.Handle) return false;
        GetWindowThreadProcessId(handle, out var processId);
        return processId != 0 && processId == target.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);
}
