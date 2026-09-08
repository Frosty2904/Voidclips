using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace VoidClip.Services;

/// <summary>
/// System-wide hotkeys via RegisterHotKey, so pads fire while Discord has focus.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private HwndSource _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private int _nextId = 0xC10;
    private readonly Dictionary<int, string> _actions = new();
    private readonly Dictionary<string, int> _byAction = new();

    /// <summary>Raised with the action key, e.g. "ClipNow" or "clip:&lt;clipId&gt;".</summary>
    public event Action<string> Pressed;
    public event Action<string, string> RegistrationFailed;   // action, gesture

    public bool Enabled { get; set; } = true;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero) _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            if (Enabled) Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    // ──────────────────────────────────────────────────────────
    public bool Register(string action, string gesture)
    {
        if (_hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(gesture)) return false;
        if (!TryParse(gesture, out var mods, out var vk)) return false;

        Unregister(action);

        var id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            RegistrationFailed?.Invoke(action, gesture);
            return false;
        }
        _actions[id] = action;
        _byAction[action] = id;
        return true;
    }

    public void Unregister(string action)
    {
        if (!_byAction.TryGetValue(action, out var id)) return;
        try { UnregisterHotKey(_hwnd, id); } catch { }
        _actions.Remove(id);
        _byAction.Remove(action);
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys.ToList())
        {
            try { UnregisterHotKey(_hwnd, id); } catch { }
        }
        _actions.Clear();
        _byAction.Clear();
    }

    // ──────────────────────────────────────────────────────────
    /// <summary>Parses gestures like "Ctrl+Alt+D1", "Shift+F9", "Win+NumPad5".</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": case "windows": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        var keyName = parts[^1];
        if (!Enum.TryParse<Key>(keyName, true, out var key)) return false;
        if (key == Key.None) return false;

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    /// <summary>Builds a gesture string from a live key event.</summary>
    public static string Describe(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;

        var sb = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Add("Win");
        sb.Add(key.ToString());
        return string.Join("+", sb);
    }

    /// <summary>Human-friendly form for display ("Ctrl+Alt+1" rather than "Ctrl+Alt+D1").</summary>
    public static string Pretty(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "";
        var parts = gesture.Split('+');
        var last = parts[^1];
        if (last.Length == 2 && last[0] == 'D' && char.IsDigit(last[1])) parts[^1] = last[1].ToString();
        else if (last.StartsWith("NumPad")) parts[^1] = "Num" + last[6..];
        else if (last == "Oem3") parts[^1] = "`";
        else if (last == "OemTilde") parts[^1] = "`";
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(Hook);
        _source = null;
    }
}
