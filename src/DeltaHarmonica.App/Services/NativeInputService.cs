using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaHarmonica.App.Services;

/// <summary>A set of harmonica notes sharing the same mouse pitch modifiers.</summary>
public sealed record InputChord(IReadOnlyList<int> KeyIndices, int OctaveShift, bool Semitone);

/// <summary>Allows the player to use a harmless recording sink during tests and previews.</summary>
public interface IInputSink
{
    void SendChord(InputChord chord);
    void SetChord(InputChord chord, IReadOnlyList<int> retriggerKeys);
    void ReleaseAll();
}

/// <summary>Emits physical keyboard scan codes and releases every input this instance owns.</summary>
public sealed class NativeInputService : IInputSink, IDisposable
{
    // Physical US-layout Z X C V B N M comma keys, in the game's ascending order.
    private static readonly ushort[] NoteScanCodes = [0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33];
    private readonly object _gate = new();
    private readonly HashSet<int> _heldNotes = [];
    private bool _leftHeld;
    private bool _rightHeld;
    private bool _middleHeld;
    private bool _disposed;

    public void SendChord(InputChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        SetChord(chord, chord.KeyIndices);
    }

    /// <summary>Updates a chord without interrupting shared sustained notes unless their pitch modifier changes.</summary>
    public void SetChord(InputChord chord, IReadOnlyList<int> retriggerKeys)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(chord.KeyIndices);
        ArgumentNullException.ThrowIfNull(retriggerKeys);
        if (chord.OctaveShift is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(chord), "OctaveShift must be -1, 0, or 1.");

        var notes = chord.KeyIndices.Distinct().Order().ToArray();
        var retriggers = retriggerKeys.ToHashSet();
        if (notes.Any(index => index is < 0 or > 7) || retriggers.Any(index => index is < 0 or > 7))
            throw new ArgumentOutOfRangeException(nameof(chord), "Key indices must be between 0 and 7.");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (notes.Length == 0)
            {
                ReleaseAllCore();
                return;
            }

            var operations = new List<InputOperation>();
            var left = chord.OctaveShift < 0;
            var right = chord.OctaveShift > 0;
            var middle = chord.Semitone;
            var modifiersChanged = left != _leftHeld || right != _rightHeld || middle != _middleHeld;
            foreach (var oldNote in _heldNotes.Order())
            {
                if (modifiersChanged || !notes.Contains(oldNote) || retriggers.Contains(oldNote))
                    operations.Add(KeyOperation(oldNote, down: false));
            }

            if (modifiersChanged)
            {
                if (_middleHeld) operations.Add(MouseOperation(MouseButton.Middle, down: false));
                if (_rightHeld) operations.Add(MouseOperation(MouseButton.Right, down: false));
                if (_leftHeld) operations.Add(MouseOperation(MouseButton.Left, down: false));
                if (left) operations.Add(MouseOperation(MouseButton.Left, down: true));
                if (right) operations.Add(MouseOperation(MouseButton.Right, down: true));
                if (middle) operations.Add(MouseOperation(MouseButton.Middle, down: true));
            }

            foreach (var note in notes)
            {
                if (modifiersChanged || !_heldNotes.Contains(note) || retriggers.Contains(note))
                    operations.Add(KeyOperation(note, down: true));
            }

            try
            {
                if (operations.Count != 0) Submit(operations);
            }
            catch
            {
                // A partially accepted SendInput call must not leave notes or modifiers down.
                try { ReleaseAllCore(); } catch { /* Preserve the original injection error. */ }
                throw;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
            ReleaseAllCore();
    }

    private void ReleaseAllCore()
    {
        var operations = new List<InputOperation>();
        // Notes must finish while their pitch modifiers are still held.
        foreach (var note in _heldNotes.Order())
            operations.Add(KeyOperation(note, down: false));
        if (_middleHeld)
            operations.Add(MouseOperation(MouseButton.Middle, down: false));
        if (_rightHeld)
            operations.Add(MouseOperation(MouseButton.Right, down: false));
        if (_leftHeld)
            operations.Add(MouseOperation(MouseButton.Left, down: false));

        if (operations.Count != 0)
            Submit(operations);
    }

    private void Submit(List<InputOperation> operations)
    {
        var inputs = operations.Select(operation => operation.Input).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        var error = Marshal.GetLastWin32Error();
        for (var index = 0; index < Math.Min((int)sent, operations.Count); index++)
            Track(operations[index]);
        if (sent != inputs.Length)
            throw new Win32Exception(error,
                "Windows 未能发送全部按键。请确认游戏处于前台，且软件与游戏具有相同权限；游戏也可能禁止模拟输入。");
    }

    private void Track(InputOperation operation)
    {
        if (operation.NoteIndex is int note)
        {
            if (operation.Down) _heldNotes.Add(note);
            else _heldNotes.Remove(note);
            return;
        }

        switch (operation.Button)
        {
            case MouseButton.Left: _leftHeld = operation.Down; break;
            case MouseButton.Right: _rightHeld = operation.Down; break;
            case MouseButton.Middle: _middleHeld = operation.Down; break;
        }
    }

    private static InputOperation KeyOperation(int note, bool down) => new(
        new NativeInput
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = NoteScanCodes[note],
                    Flags = 0x0008u | (down ? 0u : 0x0002u)
                }
            }
        }, note, null, down);

    private static InputOperation MouseOperation(MouseButton button, bool down) => new(
        new NativeInput
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = (button, down) switch
                    {
                        (MouseButton.Left, true) => 0x0002,
                        (MouseButton.Left, false) => 0x0004,
                        (MouseButton.Right, true) => 0x0008,
                        (MouseButton.Right, false) => 0x0010,
                        (MouseButton.Middle, true) => 0x0020,
                        _ => 0x0040
                    }
                }
            }
        }, null, button, down);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ReleaseAllCore();
            _disposed = true;
        }
    }

    private enum MouseButton { Left, Right, Middle }
    private readonly record struct InputOperation(NativeInput Input, int? NoteIndex, MouseButton? Button, bool Down);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] NativeInput[] inputs, int inputSize);
}
