using System.Runtime.InteropServices;

namespace Spotikey;

public enum HotkeyAction { Next, Previous, PlayPause, NowPlaying, Shuffle, Like, VolumeUp, VolumeDown, Repeat }

/// <summary>
/// Registers system-wide hotkeys with the Win32 RegisterHotKey API and raises <see cref="Pressed"/>
/// on the UI thread when one fires. Works regardless of which window has focus.
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly Dictionary<int, HotkeyAction> _registered = new();

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Human-readable problems from <see cref="Register"/> (bad syntax, key already taken).</summary>
    public List<string> Errors { get; } = new();

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
    }

    public void Register(HotkeyAction action, string binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return; // unbound on purpose

        if (!TryParse(binding, out uint mods, out uint vk))
        {
            Errors.Add($"{action}: cannot understand \"{binding}\"");
            return;
        }

        int id = (int)action + 1;
        if (!RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, vk))
        {
            Errors.Add($"{action}: \"{binding}\" is already used by another program (Win32 error {Marshal.GetLastWin32Error()})");
            return;
        }

        _registered[id] = action;
    }

    /// <summary>Parses strings like "Ctrl+Alt+Right" or "Win+Shift+Space".</summary>
    public static bool TryParse(string binding, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;

        var parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default: return false;
            }
        }

        string key = parts[^1];
        string keyName = key.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "pgup" => "PageUp",
            "pgdn" or "pgdown" => "PageDown",
            "del" => "Delete",
            "ins" => "Insert",
            "enter" => "Return",
            "plus" => "Oemplus",
            "minus" => "OemMinus",
            "comma" => "Oemcomma",
            "period" => "OemPeriod",
            var d when d.Length == 1 && char.IsDigit(d[0]) => "D" + d,
            _ => key,
        };

        if (!Enum.TryParse<Keys>(keyName, ignoreCase: true, out var k)) return false;

        vk = (uint)(k & Keys.KeyCode);
        return vk != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _registered.TryGetValue((int)m.WParam, out var action))
            Pressed?.Invoke(action);

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _registered.Keys)
            UnregisterHotKey(Handle, id);
        _registered.Clear();
        DestroyHandle();
    }
}
