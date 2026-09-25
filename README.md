# Spotikey

Spotify on your keys. Global keyboard shortcuts for the Spotify web player (open.spotify.com) in
Chrome, plus a small now-playing popup with a clickable queue. Runs as a tray icon and works no matter
which window has focus, so you never have to switch to the browser to skip, pause or check a song.

![Spotikey now-playing popup with the queue](docs/poc.png)

| Action                          | Default hotkey     | Needs extension |
|---------------------------------|--------------------|-----------------|
| Next track                      | Ctrl+Alt+Right     | no              |
| Previous / restart song         | Ctrl+Alt+Left      | no              |
| Play / pause                    | Ctrl+Alt+Down      | no              |
| Show now playing + queue        | Ctrl+Alt+Up        | queue only      |
| Shuffle on / off                | Ctrl+Alt+S         | yes             |
| Like (add to Liked Songs)       | Ctrl+Alt+L         | yes             |
| Spotify volume up               | Ctrl+Alt+PageUp    | yes             |
| Spotify volume down             | Ctrl+Alt+PageDown  | yes             |
| Repeat mode (off / all / one)   | unbound            | yes             |

Previous behaves like Spotify's own button: restarts the current song if you are a few seconds in,
otherwise jumps to the previous one.

## How it works

Two layers:

1. **Windows media session (no extension).** Chrome publishes the Spotify tab to Windows as a media
   session. The app sends next / previous / play-pause to it and reads title, artist, album and
   artwork from it. Falls back to virtual media keys if no session exists.
2. **Chrome extension (optional).** A tiny unpacked extension runs inside open.spotify.com and talks
   to the tray app over a WebSocket on 127.0.0.1:47321. It reads the queue panel and clicks Spotify's
   own shuffle / like / volume / repeat buttons. Without it, the popup shows only the current track and
   the extension hotkeys show "Chrome extension not connected".

No Spotify login, no Web API, no Premium needed.

## Build and install

Requires the .NET 8 SDK (the script looks in `%LOCALAPPDATA%\Microsoft\dotnet` first, then PATH).

```powershell
.\build.ps1
```

This publishes a single `Spotikey.exe`, copies it and the `extension` folder to
`%LOCALAPPDATA%\Spotikey`, registers it under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
so it starts with Windows, and launches it. Use `-NoAutostart` or `-NoRun` to skip those steps.

## Installing the Chrome extension

1. Open `chrome://extensions`
2. Turn on **Developer mode** (top right)
3. Click **Load unpacked** and pick `%LOCALAPPDATA%\Spotikey\extension`
   (tray menu "Set up Chrome extension..." opens that folder and copies the path)
4. Reload the open.spotify.com tab

The tray menu then shows "Chrome extension: connected". Chrome shows a "disable developer mode
extensions" bubble at startup for unpacked extensions; close it.

**After every rebuild** that touches the `extension` folder: open `chrome://extensions`, click the
reload arrow on "Spotikey Bridge", then reload the open.spotify.com tab. Chrome does not pick up
changed files on its own; a stale content script answers "Unknown action ..." in the popup.

**Clickable queue.** Click a row in the popup to jump to that song. The popup stays open while the
mouse is over it. Clicking the top part (cover, title) dismisses it.

**Queue and the side panel.** Spotify only renders the queue when its Queue panel is open. If the panel
is closed, the extension opens it, reads it, and closes it again (a brief flicker). It then remembers
the list and only re-reads when the current song is no longer in it, so most skips do not flicker.
Keep the Queue panel open in Spotify to avoid it entirely.

## Changing hotkeys

Right-click the tray icon, choose "Edit hotkeys (config.json)...", change the bindings, save, then
"Reload config". Format is `Modifier+Modifier+Key`, for example `Ctrl+Shift+Space` or `Win+Alt+N`.
An empty string unbinds an action. Modifiers: `Ctrl`, `Alt`, `Shift`, `Win`. Keys use .NET `Keys`
names (`Right`, `Space`, `F9`, `N`, `PageUp`, digits...).

Other settings in the same file: `PopupSeconds`, `ShowPopupOnAction`, `QueueRows` (default 5),
`VolumeStep` (0.1 = 10%) and `BridgePort` (must match `PORT` in `extension/background.js`).

## Troubleshooting

- **"Chrome extension not connected"**: extension not loaded, Chrome not running, or the service worker
  is asleep. It reconnects within 30 seconds; opening `chrome://extensions` and clicking the extension's
  "service worker" link wakes it immediately.
- **Shuffle / like / volume do nothing**: Spotify changed its page structure. The selectors live in
  `extension/content.js` (`SEL` at the top) and are based on `data-testid` attributes.
- **Port in use**: change `BridgePort` in config.json and `PORT` in `extension/background.js`, then
  reload both.

## Uninstall

Right-click the tray icon, untick "Start with Windows", then Exit and delete
`%LOCALAPPDATA%\Spotikey`. Remove the extension from `chrome://extensions`.
