using System.Drawing.Drawing2D;

namespace YueControl;

public sealed record QueueItem(string Title, string Artist);

/// <summary>Extra info from the Chrome extension, all optional.</summary>
public sealed record PopupExtras(
    bool BridgeConnected,
    string? Message,
    bool? Liked,
    string? Shuffle,
    IReadOnlyList<QueueItem> Queue,
    string? QueueSource,
    bool QueueRequested,
    string? Site)
{
    public static readonly PopupExtras None = new(false, null, null, null, Array.Empty<QueueItem>(), null, false, null);
}

/// <summary>
/// Small dark toast in the bottom-right corner of the screen the mouse is on. Never steals focus,
/// fades in, stays for a few seconds, fades out. Click it to dismiss early.
/// Shows the current track and, when the Chrome extension is connected, the next few in the queue.
/// </summary>
public sealed class NowPlayingPopup : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// WinForms drops WS_EX_TOPMOST on a non-activating form after Hide()/Show(), which leaves the
    /// popup painted on top but below other windows in z-order, so clicks fall through. Re-assert it.
    /// </summary>
    void EnsureTopMost()
    {
        if (IsHandleCreated)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // Layout at 96 DPI. Everything is scaled by _scale in the constructor.
    const int BaseWidth = 400;
    const int HeaderHeight = 100;
    const int QueueTitleY = 100;
    const int QueueRowsY = 120;
    const int QueueRowHeight = 22;
    const int BottomPadding = 10;
    const int MaxQueueRows = 10;

    static readonly Color Background = Color.FromArgb(24, 24, 24);
    static readonly Color Border = Color.FromArgb(64, 64, 64);
    static readonly Color SpotifyGreen = Color.FromArgb(30, 215, 96);
    static readonly Color YouTubeRed = Color.FromArgb(255, 0, 0);
    static readonly Color Dim = Color.FromArgb(150, 150, 150);
    static readonly Color RowText = Color.FromArgb(215, 215, 215);
    static readonly Color RowHover = Color.FromArgb(44, 44, 44);

    readonly PictureBox _art = new()
    {
        Location = new Point(14, 14),
        Size = new Size(72, 72),
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(40, 40, 40),
    };

    readonly Label _title = new()
    {
        Location = new Point(98, 14),
        Size = new Size(BaseWidth - 98 - 14, 26),
        Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
        ForeColor = Color.White,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    readonly Label _artist = new()
    {
        Location = new Point(98, 40),
        Size = new Size(BaseWidth - 98 - 14, 22),
        Font = new Font("Segoe UI", 9.5f),
        ForeColor = Color.FromArgb(200, 200, 200),
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    readonly Label _status = new()
    {
        Location = new Point(98, 64),
        Size = new Size(BaseWidth - 98 - 14, 20),
        Font = new Font("Segoe UI", 9f),
        ForeColor = SpotifyGreen,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    readonly Label _queueTitle = new()
    {
        Location = new Point(14, QueueTitleY),
        Size = new Size(BaseWidth - 28, 18),
        Font = new Font("Segoe UI", 8f, FontStyle.Bold),
        ForeColor = Dim,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Visible = false,
    };

    readonly Label[] _rows = new Label[MaxQueueRows];
    IReadOnlyList<QueueItem> _queue = Array.Empty<QueueItem>();

    /// <summary>Raised on the UI thread when the user clicks a queue row (index in the shown list).</summary>
    public event Action<int, QueueItem>? QueueItemClicked;

    readonly System.Windows.Forms.Timer _hideTimer = new();
    readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 15 };
    bool _fadingOut;

    /// <summary>Layout above is designed at 96 DPI; everything is multiplied by this at runtime.</summary>
    readonly float _scale;

    public NowPlayingPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(BaseWidth, HeaderHeight);
        BackColor = Background;
        Opacity = 0;
        DoubleBuffered = true;

        for (int i = 0; i < MaxQueueRows; i++)
        {
            int index = i;
            var row = new Label
            {
                Location = new Point(14, QueueRowsY + i * QueueRowHeight),
                Size = new Size(BaseWidth - 28, QueueRowHeight),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = RowText,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand,
                Visible = false,
            };
            row.MouseEnter += (_, _) => { row.BackColor = RowHover; row.ForeColor = Color.White; };
            row.MouseLeave += (_, _) => { row.BackColor = Color.Transparent; row.ForeColor = RowText; };
            row.Click += (_, _) =>
            {
                if (index < _queue.Count) QueueItemClicked?.Invoke(index, _queue[index]);
            };
            _rows[i] = row;
        }

        Controls.AddRange(new Control[] { _art, _title, _artist, _status, _queueTitle });
        Controls.AddRange(_rows);

        // Fonts are in points and already render at the monitor's DPI, but positions and sizes
        // are raw pixels, so scale those to match (e.g. 2.25x on a 225% display).
        _scale = DeviceDpi / 96f;
        if (Math.Abs(_scale - 1f) > 0.01f)
            Scale(new SizeF(_scale, _scale));

        // Clicking the header area dismisses; queue rows are clickable and handled above.
        Click += (_, _) => Dismiss();
        foreach (Control c in new Control[] { _art, _title, _artist, _status, _queueTitle })
            c.Click += (_, _) => Dismiss();

        _hideTimer.Tick += (_, _) =>
        {
            // Stay open while the mouse is over the popup so rows can be clicked.
            if (Bounds.Contains(Cursor.Position))
            {
                _hideTimer.Interval = 800;
                return;
            }
            _hideTimer.Stop();
            _fadingOut = true;
            _fadeTimer.Start();
        };

        _fadeTimer.Tick += (_, _) =>
        {
            if (_fadingOut)
            {
                Opacity -= 0.12;
                if (Opacity <= 0.01)
                {
                    _fadeTimer.Stop();
                    Hide();
                }
            }
            else
            {
                Opacity += 0.15;
                if (Opacity >= 0.99)
                {
                    Opacity = 1.0;
                    _fadeTimer.Stop();
                }
            }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    int Px(int logical) => (int)Math.Round(logical * (_scale == 0 ? 1f : _scale));

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), Px(12));
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Border, Math.Max(1f, 1.5f * _scale));
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Px(12));
        e.Graphics.DrawPath(pen, path);

        if (_queueTitle.Visible)
        {
            using var line = new Pen(Color.FromArgb(48, 48, 48), 1f);
            int y = Px(QueueTitleY) - Px(3);
            e.Graphics.DrawLine(line, Px(14), y, Width - Px(14), y);
        }
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Shows (or refreshes) the popup and restarts the auto-hide countdown.</summary>
    public void ShowInfo(NowPlayingInfo? info, PopupExtras extras, double seconds)
    {
        var oldArt = _art.Image;

        if (info == null)
        {
            _title.Text = "Nothing is playing";
            _artist.Text = "Open Spotify or YouTube Music and start a song";
            _art.Image = null;
        }
        else
        {
            _title.Text = string.IsNullOrWhiteSpace(info.Title) ? "Unknown title" : info.Title;
            _artist.Text = info.Artist;
            _art.Image = info.Art;
        }

        if (oldArt != null && !ReferenceEquals(oldArt, _art.Image)) oldArt.Dispose();

        _status.Text = BuildStatus(info, extras);
        _status.ForeColor = extras.Message != null && !extras.BridgeConnected
            ? Color.FromArgb(255, 170, 80)
            : IsYouTube(extras.Site) ? YouTubeRed : SpotifyGreen;

        // Queue rows
        _queue = extras.Queue;
        int rows = Math.Min(extras.Queue.Count, MaxQueueRows);
        for (int i = 0; i < MaxQueueRows; i++)
        {
            bool visible = i < rows;
            _rows[i].Visible = visible;
            _rows[i].BackColor = Color.Transparent; // clear stale hover highlight after relayout
            _rows[i].ForeColor = RowText;
            if (visible)
            {
                var q = extras.Queue[i];
                _rows[i].Text = string.IsNullOrEmpty(q.Artist) ? q.Title : $"{q.Title}  ·  {q.Artist}";
            }
        }

        if (rows > 0)
        {
            _queueTitle.Text = string.IsNullOrWhiteSpace(extras.QueueSource) ? "UP NEXT" : "UP NEXT  ·  " + extras.QueueSource.ToUpperInvariant();
            _queueTitle.Visible = true;
            ClientSize = new Size(Px(BaseWidth), Px(QueueRowsY + rows * QueueRowHeight + BottomPadding));
        }
        else if (extras.QueueRequested && !extras.BridgeConnected && info != null)
        {
            _queueTitle.Text = "Queue needs the Chrome extension  ·  tray menu → Set up Chrome extension";
            _queueTitle.Visible = true;
            ClientSize = new Size(Px(BaseWidth), Px(QueueTitleY + 18 + BottomPadding));
        }
        else
        {
            _queueTitle.Visible = false;
            ClientSize = new Size(Px(BaseWidth), Px(HeaderHeight));
        }
        Invalidate();

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int margin = Px(16);
        Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);

        _fadingOut = false;
        if (!Visible)
        {
            Opacity = 0;
            Show();
        }
        EnsureTopMost();
        _fadeTimer.Start();

        _hideTimer.Stop();
        _hideTimer.Interval = Math.Max(500, (int)(seconds * 1000));
        _hideTimer.Start();
    }

    static bool IsYouTube(string? site) => site is "youtube" or "ytmusic";

    static string BuildStatus(NowPlayingInfo? info, PopupExtras extras)
    {
        if (extras.Message != null) return extras.Message;
        if (info == null) return "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(info.Status)) parts.Add(info.Status);
        if (extras.Liked == true) parts.Add("♥ Liked");
        if (extras.Shuffle == "on") parts.Add("Shuffle");
        else if (extras.Shuffle == "smart") parts.Add("Smart Shuffle");
        if (parts.Count < 3 && !string.IsNullOrEmpty(info.Album) && info.Album != info.Title) parts.Add(info.Album);
        return string.Join("  ·  ", parts);
    }

    void Dismiss()
    {
        _hideTimer.Stop();
        _fadeTimer.Stop();
        Opacity = 0;
        Hide();
    }
}
