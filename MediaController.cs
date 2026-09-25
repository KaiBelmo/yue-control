using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace Spotikey;

public sealed record NowPlayingInfo(string Title, string Artist, string Album, string Status, Image? Art, string SourceApp);

/// <summary>
/// Talks to the Windows System Media Transport Controls (SMTC). Chrome registers the Spotify web player
/// there through the Media Session API, so we get next/previous/play-pause plus title, artist and
/// album art without touching Chrome at all. If no media session exists we fall back to sending
/// the virtual media keys, which any player understands.
/// </summary>
public sealed class MediaController : IDisposable
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Raised (on a background thread) when the track or playback state changes.</summary>
    public event Action? Changed;

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Attach(_manager.GetCurrentSession());
        Attach(_manager.GetCurrentSession());
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }

        Changed?.Invoke();
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Changed?.Invoke();
    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Changed?.Invoke();

    /// <summary>The session Windows considers "current" (the one media keys would hit), or any session.</summary>
    GlobalSystemMediaTransportControlsSession? Current
    {
        get
        {
            if (_manager == null) return null;
            try { return _manager.GetCurrentSession() ?? _manager.GetSessions().FirstOrDefault(); }
            catch { return null; }
        }
    }

    public async Task NextAsync()
    {
        if (!await TrySessionCommand(s => s.TrySkipNextAsync()))
            SendMediaKey(VK_MEDIA_NEXT_TRACK);
    }

    public async Task PreviousAsync()
    {
        if (!await TrySessionCommand(s => s.TrySkipPreviousAsync()))
            SendMediaKey(VK_MEDIA_PREV_TRACK);
    }

    public async Task PlayPauseAsync()
    {
        if (!await TrySessionCommand(s => s.TryTogglePlayPauseAsync()))
            SendMediaKey(VK_MEDIA_PLAY_PAUSE);
    }

    async Task<bool> TrySessionCommand(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        var session = Current;
        if (session == null) return false;
        try { return await command(session); }
        catch { return false; }
    }

    public async Task<NowPlayingInfo?> GetNowPlayingAsync()
    {
        var session = Current;
        if (session == null) return null;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            Image? art = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var winrtStream = await props.Thumbnail.OpenReadAsync();
                    using var stream = winrtStream.AsStreamForRead();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    buffer.Position = 0;
                    using var decoded = Image.FromStream(buffer);
                    art = new Bitmap(decoded); // copy so the stream can be disposed
                }
                catch { /* artwork is optional */ }
            }

            return new NowPlayingInfo(
                props.Title ?? "",
                props.Artist ?? "",
                props.AlbumTitle ?? "",
                StatusText(playback.PlaybackStatus),
                art,
                session.SourceAppUserModelId ?? "");
        }
        catch
        {
            return null;
        }
    }

    static string StatusText(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playing",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "Stopped",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "Loading",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => "Ready",
        _ => "",
    };

    public void Dispose() => Attach(null);

    // ---- Fallback: synthesize hardware media keys via SendInput ----

    const ushort VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static void SendMediaKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_EXTENDEDKEY };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
