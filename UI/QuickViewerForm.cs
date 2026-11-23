using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Explorer2.Interop;

namespace Explorer2.UI;

/// <summary>
/// F3 quick viewer in the spirit of Total Commander's Lister: a read-only window that shows the
/// file with the native Windows previewer (PDF, Office, …), as an image, as text, or as hex.
/// Keys: Esc / F3 close · N / P next / previous file · 1 Text · 3 Hex · 4 Preview (auto)
/// · W word wrap · Enter / Shift+F3 open with the default app.
/// </summary>
internal sealed class QuickViewerForm : Form
{
    private enum ViewMode
    {
        Auto,
        Text,
        Hex
    }

    private const int MaxTextBytes = 8 * 1024 * 1024;
    private const int MaxHexBytes = 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".config", ".json", ".xml",
        ".yml", ".yaml", ".toml", ".cs", ".csproj", ".sln", ".props", ".targets", ".c", ".h", ".cpp", ".hpp",
        ".js", ".ts", ".jsx", ".tsx", ".css", ".scss", ".html", ".htm", ".py", ".ps1", ".psm1", ".bat", ".cmd",
        ".sh", ".sql", ".java", ".go", ".rs", ".rb", ".php", ".lua", ".ahk", ".reg", ".gitignore", ".editorconfig",
        ".manifest", ".resx", ".nfo", ".srt", ".vtt"
    };

    private readonly IReadOnlyList<string> _files;
    private readonly Panel _contentHost;
    private readonly TextBox _textBox;
    private readonly PictureBox _pictureBox;
    private readonly Panel _previewPanel;
    private readonly Label _messageLabel;
    private readonly Label _statusLabel;
    private readonly ShellPreviewHost _previewHost = new();
    private readonly Font _monoFont;
    private readonly Font _rtlMonoFont;
    private readonly Font _uiFont;
    private readonly bool _isDarkMode;

    private int _index;
    private ViewMode _mode = ViewMode.Auto;
    private string _shownAs = string.Empty;

    public QuickViewerForm(IReadOnlyList<string> files, int startIndex, bool isDarkMode, Icon? icon, Form owner)
    {
        _files = files;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, files.Count - 1));
        _isDarkMode = isDarkMode;

        Color bg = isDarkMode ? Color.FromArgb(28, 28, 32) : Color.White;
        Color fg = isDarkMode ? Color.FromArgb(240, 240, 244) : Color.FromArgb(24, 24, 28);
        Color muted = isDarkMode ? Color.FromArgb(164, 164, 174) : Color.FromArgb(96, 96, 106);
        Color statusBg = isDarkMode ? Color.FromArgb(40, 40, 46) : Color.FromArgb(243, 243, 246);

        _monoFont = new Font("Consolas", 10f, FontStyle.Regular);
        _rtlMonoFont = new Font("Courier New", 10.5f, FontStyle.Regular);
        _uiFont = new Font("Segoe UI", 9f, FontStyle.Regular);

        Font = _uiFont;
        BackColor = bg;
        ForeColor = fg;
        KeyPreview = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        if (icon is not null)
        {
            Icon = icon;
        }

        Rectangle area = Screen.FromControl(owner).WorkingArea;
        Size size = new((int)(area.Width * 0.72), (int)(area.Height * 0.82));
        Rectangle ownerBounds = owner.Bounds;
        Point location = new(
            ownerBounds.Left + (ownerBounds.Width - size.Width) / 2,
            ownerBounds.Top + (ownerBounds.Height - size.Height) / 2);
        location.X = Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - size.Width));
        location.Y = Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        Bounds = new Rectangle(location, size);
        MinimumSize = new Size(420, 300);

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = bg };

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            BackColor = bg,
            ForeColor = fg,
            Font = _monoFont,
            HideSelection = false,
            MaxLength = 0,
            Visible = false
        };

        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Visible = false
        };
        _pictureBox.Resize += (_, _) => UpdatePictureSizeMode();

        _previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = bg, Visible = false };
        _previewPanel.Resize += (_, _) => _previewHost.Resize();

        _messageLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = muted,
            BackColor = bg,
            Visible = false
        };

        _contentHost.Controls.Add(_textBox);
        _contentHost.Controls.Add(_pictureBox);
        _contentHost.Controls.Add(_previewPanel);
        _contentHost.Controls.Add(_messageLabel);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(10, 0, 10, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            BackColor = statusBg,
            ForeColor = muted
        };

        Controls.Add(_contentHost);
        Controls.Add(_statusLabel);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Match the system theme on the native title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
        try
        {
            int dark = _isDarkMode ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
        catch
        {
            // Older Windows builds
        }

        Configuration.AppTheme.AllowDarkForWindowTree(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ShowCurrentFile();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        bool handled = true;
        switch (e.KeyCode)
        {
            case Keys.Escape:
            case Keys.F3 when !e.Shift:
                Close();
                break;
            case Keys.F3 when e.Shift:
            case Keys.Return:
                OpenWithDefaultApp();
                break;
            case Keys.N:
            case Keys.PageDown when e.Control:
                Step(+1);
                break;
            case Keys.P:
            case Keys.PageUp when e.Control:
                Step(-1);
                break;
            case Keys.D1:
            case Keys.NumPad1:
                SetMode(ViewMode.Text);
                break;
            case Keys.D3:
            case Keys.NumPad3:
                SetMode(ViewMode.Hex);
                break;
            case Keys.D4:
            case Keys.NumPad4:
                SetMode(ViewMode.Auto);
                break;
            case Keys.W:
                _textBox.WordWrap = !_textBox.WordWrap;
                UpdateStatus();
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void Step(int delta)
    {
        if (_files.Count < 2)
        {
            return;
        }

        int next = _index + delta;
        if (next < 0 || next >= _files.Count)
        {
            return;
        }

        _index = next;
        ShowCurrentFile();
    }

    private void SetMode(ViewMode mode)
    {
        if (_mode != mode)
        {
            _mode = mode;
            ShowCurrentFile();
        }
    }

    private void OpenWithDefaultApp()
    {
        if (_files.Count == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_files[_index]) { UseShellExecute = true });
        }
        catch
        {
            // No associated app
        }
    }

    private void ShowCurrentFile()
    {
        ResetContent();

        if (_files.Count == 0)
        {
            ShowMessage("Nothing to view.");
            return;
        }

        string path = _files[_index];
        Text = $"{Path.GetFileName(path)} — Explorer2 Viewer";

        if (!File.Exists(path))
        {
            ShowMessage("The file no longer exists.");
            UpdateStatus();
            return;
        }

        try
        {
            switch (_mode)
            {
                case ViewMode.Text:
                    ShowText(path);
                    break;
                case ViewMode.Hex:
                    ShowHex(path);
                    break;
                default:
                    ShowAuto(path);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"Can't show this file:\n{ex.Message}");
        }

        UpdateStatus();
    }

    private void ShowAuto(string path)
    {
        string ext = Path.GetExtension(path);

        if (ImageExtensions.Contains(ext) && TryShowImage(path))
        {
            return;
        }

        if (TextExtensions.Contains(ext))
        {
            ShowText(path);
            return;
        }

        if (ShellPreviewHost.HasHandler(path) && TryShowPreview(path))
        {
            return;
        }

        if (LooksBinary(path))
        {
            ShowHex(path);
        }
        else
        {
            ShowText(path);
        }
    }

    private bool TryShowImage(string path)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image img = Image.FromStream(fs);
            _pictureBox.Image = new Bitmap(img);
            _pictureBox.Visible = true;
            _pictureBox.BringToFront();
            UpdatePictureSizeMode();
            _shownAs = $"Image {img.Width} × {img.Height}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryShowPreview(string path)
    {
        ShowMessage("Loading preview…");
        _messageLabel.Update();

        _previewPanel.Visible = true;
        _previewPanel.BringToFront();
        _contentHost.PerformLayout();

        if (_previewHost.TryLoad(path, _previewPanel))
        {
            _messageLabel.Visible = false;
            _shownAs = "Preview";
            // Keep keyboard focus on the viewer so its keys keep working
            ActiveControl = null;
            Focus();
            return true;
        }

        _previewPanel.Visible = false;
        _messageLabel.Visible = false;
        return false;
    }

    private void ShowText(string path)
    {
        byte[] bytes = ReadHead(path, MaxTextBytes, out bool truncated);
        string text = Decode(bytes, out string encodingName);
        text = text.Replace('\0', ' ');
        text = Regex.Replace(text, "(?<!\r)\n", "\r\n");
        if (truncated)
        {
            text += $"\r\n\r\n… (showing the first {MaxTextBytes / (1024 * 1024)} MB)";
        }

        // Consolas has no Hebrew/Arabic glyphs (Windows substitutes a much smaller font);
        // Courier New is monospaced and covers them.
        _textBox.Font = Regex.IsMatch(text, "[֐-ۿ]") ? _rtlMonoFont : _monoFont;
        _textBox.Text = text;
        _textBox.SelectionStart = 0;
        _textBox.SelectionLength = 0;
        _textBox.Visible = true;
        _textBox.BringToFront();
        _textBox.Focus();
        _shownAs = $"Text ({encodingName})";
    }

    private void ShowHex(string path)
    {
        byte[] bytes = ReadHead(path, MaxHexBytes, out bool truncated);
        StringBuilder sb = new(bytes.Length * 4 + 64);
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            sb.Append(offset.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (offset + i < bytes.Length)
                {
                    sb.Append(bytes[offset + i].ToString("X2")).Append(' ');
                }
                else
                {
                    sb.Append("   ");
                }

                if (i == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(' ');
            for (int i = 0; i < 16 && offset + i < bytes.Length; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append("\r\n");
        }

        if (truncated)
        {
            sb.Append($"\r\n… (showing the first {MaxHexBytes / 1024} KB)");
        }

        _textBox.WordWrap = false;
        _textBox.Font = _monoFont;
        _textBox.Text = sb.ToString();
        _textBox.SelectionStart = 0;
        _textBox.SelectionLength = 0;
        _textBox.Visible = true;
        _textBox.BringToFront();
        _textBox.Focus();
        _shownAs = "Hex";
    }

    private void ShowMessage(string message)
    {
        _messageLabel.Text = message;
        _messageLabel.Visible = true;
        _messageLabel.BringToFront();
    }

    private void ResetContent()
    {
        _previewHost.Unload();
        _previewPanel.Visible = false;
        _textBox.Visible = false;
        _textBox.Clear();
        _messageLabel.Visible = false;
        _pictureBox.Visible = false;
        if (_pictureBox.Image is not null)
        {
            Image old = _pictureBox.Image;
            _pictureBox.Image = null;
            old.Dispose();
        }

        _shownAs = string.Empty;
    }

    private void UpdatePictureSizeMode()
    {
        if (_pictureBox.Image is not { } img)
        {
            return;
        }

        _pictureBox.SizeMode = img.Width > _pictureBox.ClientSize.Width || img.Height > _pictureBox.ClientSize.Height
            ? PictureBoxSizeMode.Zoom
            : PictureBoxSizeMode.CenterImage;
    }

    private void UpdateStatus()
    {
        if (_files.Count == 0)
        {
            _statusLabel.Text = string.Empty;
            return;
        }

        string path = _files[_index];
        string info = string.Empty;
        try
        {
            FileInfo fi = new(path);
            if (fi.Exists)
            {
                info = $"{FormatSize(fi.Length)}   {fi.LastWriteTime:g}";
            }
        }
        catch
        {
            // Ignore
        }

        string wrap = _textBox.Visible && _shownAs != "Hex" && _textBox.WordWrap ? " · wrap" : string.Empty;
        _statusLabel.Text =
            $"{_index + 1} / {_files.Count}    {Path.GetFileName(path)}    {info}    [{_shownAs}{wrap}]" +
            "        N / P  next / prev  ·  1 Text  ·  3 Hex  ·  4 Auto  ·  W Wrap  ·  Enter Open  ·  Esc Close";
    }

    private static byte[] ReadHead(string path, int maxBytes, out bool truncated)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        int count = (int)Math.Min(length, maxBytes);
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buffer, read, count - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        truncated = length > maxBytes;
        return read == count ? buffer : buffer[..read];
    }

    private static bool LooksBinary(string path)
    {
        byte[] head = ReadHead(path, 8192, out _);
        int controls = 0;
        foreach (byte b in head)
        {
            if (b == 0)
            {
                return true;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                controls++;
            }
        }

        return head.Length > 0 && controls > head.Length / 10;
    }

    private static string Decode(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "UTF-8";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "UTF-16 LE";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "UTF-16 BE";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            encodingName = "UTF-8";
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: use the system ANSI code page (e.g. Windows-1255 for Hebrew)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            encodingName = ansi.WebName;
            return ansi.GetString(bytes);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024.0:0.##} GB";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewHost.Dispose();
            if (_pictureBox.Image is not null)
            {
                _pictureBox.Image.Dispose();
                _pictureBox.Image = null;
            }

            _monoFont.Dispose();
            _rtlMonoFont.Dispose();
            _uiFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
