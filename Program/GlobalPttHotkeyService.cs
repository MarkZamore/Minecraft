using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Minecraft;

public sealed class GlobalPttHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int XButton1 = 1;
    private const int XButton2 = 2;

    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly Action<bool> _stateChanged;
    private IntPtr _keyboardHookId;
    private IntPtr _mouseHookId;
    private PttInputBinding _binding = PttInputBinding.Default;
    private bool _isPressed;
    private bool _disposed;

    public GlobalPttHotkeyService(Key key, Action<bool> stateChanged)
        : this($"Key:{key}", stateChanged)
    {
    }

    public GlobalPttHotkeyService(string binding, Action<bool> stateChanged)
    {
        _stateChanged = stateChanged;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        SetBinding(binding);
        _keyboardHookId = SetHook(WhKeyboardLl, _keyboardProc);
        _mouseHookId = SetHook(WhMouseLl, _mouseProc);
    }

    public string BindingText => _binding.ToString();

    public void SetKey(Key key) => SetBinding($"Key:{(key == Key.None || key == Key.System ? Key.V : key)}");

    public void SetBinding(string binding)
    {
        _binding = PttInputBinding.Parse(binding);
        ReleaseIfPressed();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed && _binding.Kind == PttInputKind.Key)
        {
            var message = wParam.ToInt32();
            if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                var vkCode = Marshal.ReadInt32(lParam);
                if (vkCode == _binding.VirtualKey)
                {
                    SetPressed(message is WmKeyDown or WmSysKeyDown);
                }
            }
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed && _binding.Kind == PttInputKind.Mouse)
        {
            var message = wParam.ToInt32();
            if (message is WmXButtonDown or WmXButtonUp)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var xButton = (int)((data.MouseData >> 16) & 0xffff);
                if (xButton == _binding.MouseXButton)
                {
                    SetPressed(message == WmXButtonDown);
                }
            }
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private void SetPressed(bool pressed)
    {
        if (_isPressed == pressed)
        {
            return;
        }

        _isPressed = pressed;
        _stateChanged(pressed);
    }

    private void ReleaseIfPressed()
    {
        if (!_isPressed)
        {
            return;
        }

        _isPressed = false;
        _stateChanged(false);
    }

    private static IntPtr SetHook(int hookKind, LowLevelProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = GetModuleHandle(currentModule?.ModuleName);
        var hook = SetWindowsHookEx(hookKind, proc, moduleHandle, 0);
        if (hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not register global push-to-talk input hook.");
        }

        return hook;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseIfPressed();
        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

public enum PttInputKind
{
    Key,
    Mouse
}

public sealed record PttInputBinding(PttInputKind Kind, Key Key, int MouseXButton)
{
    public static PttInputBinding Default { get; } = new(PttInputKind.Key, Key.V, 0);

    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);

    public static PttInputBinding Parse(string? value)
    {
        var binding = value?.Trim();
        if (string.IsNullOrWhiteSpace(binding))
        {
            return Default;
        }

        if (binding.Equals("Mouse:XButton1", StringComparison.OrdinalIgnoreCase))
        {
            return new PttInputBinding(PttInputKind.Mouse, Key.None, 1);
        }

        if (binding.Equals("Mouse:XButton2", StringComparison.OrdinalIgnoreCase))
        {
            return new PttInputBinding(PttInputKind.Mouse, Key.None, 2);
        }

        if (binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
        {
            var keyText = binding[4..];
            if (Enum.TryParse<Key>(keyText, ignoreCase: true, out var key) && key != Key.None && key != Key.System)
            {
                return new PttInputBinding(PttInputKind.Key, key, 0);
            }
        }

        return Default;
    }

    public override string ToString()
    {
        return Kind == PttInputKind.Mouse
            ? $"Mouse:XButton{MouseXButton}"
            : $"Key:{Key}";
    }

    public string DisplayName => Kind == PttInputKind.Mouse ? $"XButton{MouseXButton}" : Key.ToString();
}
