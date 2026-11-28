using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Explorer2.UI;

/// <summary>
/// The tour's buttons, in a small bar at the bottom right of the demo window: Back (restart the
/// chapter, or the one before), Play / Pause, Next chapter, Captions, Voice and Close. Clicking it
/// doesn't take the focus from the window.
/// </summary>
internal sealed class TourControlBar : Form
{
    internal enum Command { Back, PlayPause, Next, Captions, Voice, Close }

    private sealed record Button(Command Command, Rectangle Bounds);

    private readonly TourScript _script;
    private readonly ToolTip _tip = new();
    private readonly Font _labelFont;
    private readonly Font _glyphFont;
    private List<Button> _buttons = new();
    private int _hot = -1;
    private int _pressed = -1;
    private float _scale = 1f;

    private string _label = "";
    private bool _paused;
    private bool _captions = true;
    private bool _voice = true;
    private bool _voiceAvailable = true;
    private bool _canNext = true;

    public event Action<Command>? CommandClicked;

    public TourControlBar(TourScript script)
    {
        _script = script;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 36);
        DoubleBuffered = true;
        string glyphFamily = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";
        _glyphFont = new Font(glyphFamily, 11f);
        _labelFont = new Font("Segoe UI", 9f);
        Text = script.Ui("title");
        AccessibleName = script.Ui("title");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    public void SetState(string label, bool paused, bool captions, bool voice, bool voiceAvailable, bool canNext)
    {
        if (label == _label && paused == _paused && captions == _captions && voice == _voice &&
            voiceAvailable == _voiceAvailable && canNext == _canNext)
        {
            return;
        }

        _label = label;
        _paused = paused;
        _captions = captions;
        _voice = voice;
        _voiceAvailable = voiceAvailable;
        _canNext = canNext;
        Relayout();
    }

    /// <summary>Places the bar at the bottom right of <paramref name="window"/>, above its status bar.</summary>
    public void PlaceIn(Rectangle window, float scale)
    {
        if (Math.Abs(scale - _scale) > 0.01f)
        {
            _scale = scale;
            Relayout();
        }

        Point location = new(window.Right - Width - S(22), window.Bottom - Height - S(44));
        if (Location != location)
        {
            Location = location;
        }
    }

    private int S(float v) => (int)Math.Round(v * _scale);

    private void Relayout()
    {
        int h = S(40), b = S(36), x = S(12);
        int labelWidth;
        using (Graphics g = CreateGraphics())
        {
            labelWidth = TextRenderer.MeasureText(g, _label, _labelFont).Width + S(8);
        }

        x += labelWidth;
        List<Button> buttons = new();
        foreach (Command c in new[] { Command.Back, Command.PlayPause, Command.Next, Command.Captions, Command.Voice, Command.Close })
        {
            if (c is Command.Captions or Command.Close)
            {
                x += S(10); // gap between groups
            }

            buttons.Add(new Button(c, new Rectangle(x, (h - b) / 2, b, b)));
            x += b + S(2);
        }

        _buttons = buttons;
        Size = new Size(x + S(8), h);
        Invalidate();
    }

    private string Glyph(Command c) => c switch
    {
        Command.Back => "",
        Command.PlayPause => _paused ? "" : "",
        Command.Next => "",
        Command.Captions => "",
        Command.Voice => _voice && _voiceAvailable ? "" : "",
        _ => ""
    };

    private string Tip(Command c) => c switch
    {
        Command.Back => _script.Ui("back"),
        Command.PlayPause => _script.Ui(_paused ? "play" : "pause"),
        Command.Next => _script.Ui("next"),
        Command.Captions => _script.Ui(_captions ? "captionsOff" : "captionsOn"),
        Command.Voice => _script.Ui(!_voiceAvailable ? "noVoice" : _voice ? "voiceOff" : "voiceOn"),
        _ => _script.Ui("close")
    };

    private bool IsEnabled(Command c) => c switch
    {
        Command.Next => _canNext,
        Command.Voice => _voiceAvailable,
        _ => true
    };

    private bool IsOn(Command c) => c switch
    {
        Command.Captions => _captions,
        Command.Voice => _voice && _voiceAvailable,
        _ => false
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color fg = Color.FromArgb(240, 240, 244);
        Color dim = Color.FromArgb(110, 110, 118);
        Color accent = Color.FromArgb(0, 120, 212);

        Rectangle labelRect = new(S(12), 0, (_buttons.FirstOrDefault()?.Bounds.Left ?? S(12)) - S(16), Height);
        TextRenderer.DrawText(g, _label, _labelFont, labelRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        for (int i = 0; i < _buttons.Count; i++)
        {
            Button button = _buttons[i];
            bool enabled = IsEnabled(button.Command);
            if (IsOn(button.Command) || (enabled && (i == _hot || i == _pressed)))
            {
                Color fill = IsOn(button.Command)
                    ? Color.FromArgb(i == _hot ? 255 : 200, accent)
                    : Color.FromArgb(i == _pressed ? 80 : 56, 255, 255, 255);
                using GraphicsPath path = IconFactory.CreateRoundedRect(Rectangle.Inflate(button.Bounds, -S(2), -S(2)), S(5));
                using SolidBrush brush = new(fill);
                g.FillPath(brush, path);
            }

            TextRenderer.DrawText(g, Glyph(button.Command), _glyphFont, button.Bounds, enabled ? fg : dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private int HitTest(Point p) => _buttons.FindIndex(b => b.Bounds.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit != _hot)
        {
            _hot = hit;
            Invalidate();
            if (hit >= 0)
            {
                Rectangle r = _buttons[hit].Bounds;
                _tip.Show(Tip(_buttons[hit].Command), this, r.Left, -S(30), 2500);
            }
            else
            {
                _tip.Hide(this);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = -1;
        _pressed = -1;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = HitTest(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int hit = HitTest(e.Location);
        int pressed = _pressed;
        _pressed = -1;
        Invalidate();
        if (e.Button == MouseButtons.Left && hit >= 0 && hit == pressed && IsEnabled(_buttons[hit].Command))
        {
            CommandClicked?.Invoke(_buttons[hit].Command);
            if (!IsDisposed && _hot >= 0 && _hot < _buttons.Count)
            {
                Rectangle r = _buttons[_hot].Bounds;
                _tip.Show(Tip(_buttons[_hot].Command), this, r.Left, -S(30), 2500);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _labelFont.Dispose();
            _glyphFont.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
