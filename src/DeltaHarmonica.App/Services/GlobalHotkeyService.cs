using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeltaHarmonica.App.Services;

public enum HotkeyAction { TogglePause, Previous, Next, Stop }

public sealed record HotkeySettings(
    string Pause = "F8", string Previous = "F6", string Next = "F7", string Stop = "F9");

/// <summary>Registers global keyboard shortcuts on the WinUI window's owning UI thread.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmNcDestroy = 0x0082;
    private const uint ModNoRepeat = 0x4000;
    private static int s_nextHotkeyId = 0x5000;
    private static long s_nextSubclassId;
    private readonly nint _windowHandle;
    private readonly uint _ownerThreadId;
    private readonly nuint _subclassId;
    private readonly SubclassProcedure _subclassProcedure;
    private Dictionary<int, RegisteredHotkey> _registrations = [];
    private bool _disposed;

    public event Action<HotkeyAction>? Triggered;
    public HotkeySettings? CurrentSettings { get; private set; }

    public GlobalHotkeyService(nint windowHandle)
    {
        if (windowHandle == 0) throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        _windowHandle = windowHandle;
        _ownerThreadId = GetCurrentThreadId();
        _subclassId = (nuint)Interlocked.Increment(ref s_nextSubclassId);
        _subclassProcedure = OnWindowMessage;
        if (!SetWindowSubclass(_windowHandle, _subclassProcedure, _subclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法接收窗口快捷键消息。");
    }

    /// <returns>Null on success, otherwise a user-facing error. Existing shortcuts remain active on failure.</returns>
    public string? Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed) return "快捷键服务已关闭。";
        if (GetCurrentThreadId() != _ownerThreadId) return "请在软件的 UI 线程中修改快捷键。";
        var validationError = ParseSettings(settings, out var parsed);
        if (validationError is not null) return validationError;

        var newRegistrations = new Dictionary<int, RegisteredHotkey>();
        var newlyRegisteredIds = new List<int>();
        foreach (var requested in parsed)
        {
            // Reuse unchanged key combinations, including combinations reassigned to another action.
            // Register every genuinely new combination before removing any existing registration.
            var reusable = _registrations.FirstOrDefault(pair => pair.Value.Chord == requested.Chord);
            if (reusable.Value is not null)
            {
                newRegistrations[reusable.Key] = requested;
                continue;
            }

            var id = Interlocked.Increment(ref s_nextHotkeyId);
            if (id > 0xBFFF)
            {
                foreach (var createdId in newlyRegisteredIds) UnregisterHotKey(_windowHandle, createdId);
                return "快捷键注册次数已达上限，请重启软件后再修改。";
            }
            if (!RegisterHotKey(_windowHandle, id, requested.Chord.Modifiers | ModNoRepeat, requested.Chord.Key))
            {
                var error = Marshal.GetLastWin32Error();
                foreach (var createdId in newlyRegisteredIds) UnregisterHotKey(_windowHandle, createdId);
                return $"无法注册{ActionName(requested.Action)}快捷键“{requested.Display}”：可能已被其他软件或 Windows 占用（错误 {error}）。原快捷键继续生效。";
            }
            newlyRegisteredIds.Add(id);
            newRegistrations[id] = requested;
        }

        foreach (var oldId in _registrations.Keys.Where(id => !newRegistrations.ContainsKey(id)))
            UnregisterHotKey(_windowHandle, oldId);
        _registrations = newRegistrations;
        CurrentSettings = settings;
        return null;
    }

    public static string? Validate(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ParseSettings(settings, out _);
    }

    /// <summary>Waits for the physical shortcut chord to finish before starting simulated game notes.</summary>
    /// <remarks>Call on the owning UI thread so the registered chord can be captured before the first await.</remarks>
    public async Task<bool> WaitForReleaseAsync(HotkeyAction action, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (GetCurrentThreadId() != _ownerThreadId)
            throw new InvalidOperationException("Shortcut release waiting must begin on the owning UI thread.");
        if (_disposed) return false;
        var registration = _registrations.Values.FirstOrDefault(item => item.Action == action);
        if (registration is null) return false;
        var chord = registration.Chord;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!IsChordHeld(chord)) return true;
            if (Stopwatch.GetTimestamp() >= deadline) return false;
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private static bool IsChordHeld(KeyChord chord) =>
        IsKeyHeld(chord.Key) ||
        ((chord.Modifiers & 0x0001) != 0 && IsKeyHeld(0x12)) ||
        ((chord.Modifiers & 0x0002) != 0 && IsKeyHeld(0x11)) ||
        ((chord.Modifiers & 0x0004) != 0 && IsKeyHeld(0x10)) ||
        ((chord.Modifiers & 0x0008) != 0 && (IsKeyHeld(0x5B) || IsKeyHeld(0x5C)));

    private static bool IsKeyHeld(uint key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static string? ParseSettings(HotkeySettings settings, out List<RegisteredHotkey> parsed)
    {
        parsed = [];
        var requested = new (HotkeyAction Action, string Text)[]
        {
            (HotkeyAction.TogglePause, settings.Pause), (HotkeyAction.Previous, settings.Previous),
            (HotkeyAction.Next, settings.Next), (HotkeyAction.Stop, settings.Stop)
        };
        var occupied = new Dictionary<KeyChord, HotkeyAction>();
        foreach (var (action, text) in requested)
        {
            var error = ParseChord(text, out var chord);
            if (error is not null) return $"{ActionName(action)}快捷键：{error}";
            if (occupied.TryGetValue(chord, out var otherAction))
                return $"{ActionName(action)}与{ActionName(otherAction)}不能使用相同快捷键。";
            occupied[chord] = action;
            parsed.Add(new RegisteredHotkey(action, chord, text.Trim()));
        }
        return null;
    }

    private static string? ParseChord(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return "不能为空，例如 F8 或 Ctrl+Alt+F8。";
        uint modifiers = 0;
        uint key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var token = raw.ToUpperInvariant();
            if (token.Length == 0) return "格式无效，例如 F8 或 Ctrl+Alt+F8。";
            var modifier = token switch
            {
                "ALT" => 0x0001u, "CTRL" or "CONTROL" => 0x0002u,
                "SHIFT" => 0x0004u, "WIN" or "WINDOWS" => 0x0008u, _ => 0u
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) return $"修饰键 {raw} 重复。";
                modifiers |= modifier;
                continue;
            }
            if (key != 0) return "只能包含一个普通按键，以及 Ctrl、Alt、Shift、Win 修饰键。";
            key = ParseVirtualKey(token);
            if (key == 0) return $"不支持按键“{raw}”。可使用 F1–F24、字母、数字或方向键。";
        }

        if (key == 0) return "不能只使用 Ctrl、Alt、Shift 或 Win 等修饰键。";
        if (modifiers == 0 && (key is 0x5A or 0x58 or 0x43 or 0x56 or 0x42 or 0x4E or 0x4D or 0xBC))
            return "z x c v b n m , 用于演奏；快捷键请添加 Ctrl/Alt/Shift 或改用功能键。";
        chord = new KeyChord(modifiers, key);
        return null;
    }

    private static uint ParseVirtualKey(string token)
    {
        if (token.Length == 1 && (token[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            return token[0];
        if (token.StartsWith('F') && int.TryParse(token.AsSpan(1), out var function) && function is >= 1 and <= 24)
            return (uint)(0x70 + function - 1);
        if (token.StartsWith("NUMPAD", StringComparison.Ordinal) &&
            int.TryParse(token.AsSpan(6), out var number) && number is >= 0 and <= 9)
            return (uint)(0x60 + number);
        return token switch
        {
            "ESC" or "ESCAPE" => 0x1B, "SPACE" or "SPACEBAR" => 0x20,
            "TAB" => 0x09, "ENTER" or "RETURN" => 0x0D, "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E, "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22, "LEFT" or "ARROWLEFT" => 0x25,
            "UP" or "ARROWUP" => 0x26, "RIGHT" or "ARROWRIGHT" => 0x27,
            "DOWN" or "ARROWDOWN" => 0x28, "," or "COMMA" or "OEMCOMMA" => 0xBC,
            "." or "PERIOD" or "OEMPERIOD" => 0xBE, "MINUS" or "OEMMINUS" => 0xBD,
            "PLUS" or "OEMPLUS" => 0xBB, "MULTIPLY" => 0x6A, "ADD" => 0x6B,
            "SUBTRACT" => 0x6D, "DECIMAL" => 0x6E, "DIVIDE" => 0x6F,
            "PAUSE" => 0x13, "PRINTSCREEN" => 0x2C, "SCROLLLOCK" => 0x91,
            _ => 0
        };
    }

    private nint OnWindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (message == WmHotkey && _registrations.TryGetValue(unchecked((int)wParam), out var registered))
        {
            try { Triggered?.Invoke(registered.Action); }
            catch (Exception exception) { Trace.TraceError("Global hotkey callback failed: {0}", exception); }
            return 0;
        }
        if (message == WmNcDestroy) DisposeCore();
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.TogglePause => "暂停/继续", HotkeyAction.Previous => "上一首",
        HotkeyAction.Next => "下一首", _ => "停止"
    };

    public void Dispose()
    {
        if (_disposed) return;
        if (GetCurrentThreadId() != _ownerThreadId)
            throw new InvalidOperationException("GlobalHotkeyService must be disposed on its owning UI thread.");
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _registrations.Keys) UnregisterHotKey(_windowHandle, id);
        _registrations.Clear();
        RemoveWindowSubclass(_windowHandle, _subclassProcedure, _subclassId);
        Triggered = null;
    }

    private readonly record struct KeyChord(uint Modifiers, uint Key);
    private sealed record RegisteredHotkey(HotkeyAction Action, KeyChord Chord, string Display);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint referenceData);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
