namespace Explorer2.UI;

internal sealed class PreviewPaneControl : Panel
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tif", ".tiff"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".cs", ".ps1", ".log", ".csv", ".ini", ".yml", ".yaml",
        ".config", ".csproj", ".sln", ".bat", ".cmd", ".html", ".css", ".js", ".ts", ".py", ".sql"
    };

    private readonly Label _titleLabel;
    private readonly Label _metaLabel;
    private readonly PictureBox _imageBox;
    private readonly TextBox _textBox;
    private readonly Label _emptyLabel;
    private readonly Panel _handlerPanel;
    private readonly ShellPreviewHost _shellPreview = new();
    private readonly Font _titleFont;
    private readonly Font _metaFont;
    private readonly Font _monoFont;

    private string? _lastPath;

    public PreviewPaneControl(bool isDarkMode)
    {
        Color bg = isDarkMode ? Color.FromArgb(28, 28, 32) : Color.FromArgb(249, 249, 252);
        Color fg = isDarkMode ? Color.FromArgb(242, 242, 246) : Color.FromArgb(24, 24, 28);
        Color muted = isDarkMode ? Color.FromArgb(164, 164, 174) : Color.FromArgb(100, 100, 110);
        Color border = isDarkMode ? Color.FromArgb(56, 56, 62) : Color.FromArgb(218, 218, 224);

        _titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        _metaFont = new Font("Segoe UI", 8.75f, FontStyle.Regular);
        _monoFont = new Font("Consolas", 9f, FontStyle.Regular);

        Dock = DockStyle.Right;
        Width = 290;
        BackColor = bg;
        ForeColor = fg;
        Visible = false;

        Panel leftBorder = new()
        {
            Dock = DockStyle.Left,
            Width = 1,
            BackColor = border
        };

        Panel headerPanel = new()
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(12, 10, 12, 6),
            BackColor = bg
        };

        _titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Font = _titleFont,
            ForeColor = fg,
            AutoEllipsis = true,
            Text = "Preview"
        };

        _metaLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            Font = _metaFont,
            ForeColor = muted,
            AutoEllipsis = true,
            Text = "Select a file to preview"
        };

        headerPanel.Controls.Add(_metaLabel);
        headerPanel.Controls.Add(_titleLabel);

        _imageBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = bg,
            Padding = new Padding(12),
            Visible = false
        };

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = bg,
            ForeColor = fg,
            Font = _monoFont,
            Visible = false
        };

        _emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = muted,
            Font = _metaFont,
            Text = "Select a file to preview.",
            Padding = new Padding(16)
        };

        // Host surface for native Windows preview handlers (PDF, Office, mail, ...)
        _handlerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            Visible = false
        };
        _handlerPanel.Resize += (_, _) => _shellPreview.Resize();

        Controls.Add(_emptyLabel);
        Controls.Add(_handlerPanel);
        Controls.Add(_textBox);
        Controls.Add(_imageBox);
        Controls.Add(headerPanel);
        Controls.Add(leftBorder);
    }

    public void UpdatePreview(string? path)
    {
        if (!Visible || string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastPath = path;
        ClearImage();
        UnloadShellPreview();

        if (string.IsNullOrWhiteSpace(path))
        {
            ShowEmpty("Preview", "Select a file to preview.", "No item selected.");
            return;
        }

        if (Directory.Exists(path))
        {
            DirectoryInfo dir = new(path);
            ShowEmpty(dir.Name, $"File folder • Modified {dir.LastWriteTime:g}", "Folder preview is not available.");
            return;
        }

        if (!File.Exists(path))
        {
            ShowEmpty("Preview", string.Empty, "No preview available.");
            return;
        }

        FileInfo file = new(path);
        string meta = $"{FormatSize(file.Length)} • {file.LastWriteTime:g}";
        string ext = file.Extension;

        if (ShellPreviewHost.HasHandler(path) && TryShowShellPreview(path, file.Name, meta))
        {
            return;
        }

        if (ImageExtensions.Contains(ext))
        {
            try
            {
                using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using Image img = Image.FromStream(fs);
                _imageBox.Image = new Bitmap(img);
                _titleLabel.Text = file.Name;
                _metaLabel.Text = $"{img.Width} × {img.Height} • {meta}";
                _imageBox.Visible = true;
                _textBox.Visible = false;
                _emptyLabel.Visible = false;
                return;
            }
            catch
            {
                // Fall back to empty preview if image is unreadable
            }
        }

        if (TextExtensions.Contains(ext))
        {
            try
            {
                const int maxBytes = 64 * 1024;
                using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[(int)Math.Min(maxBytes, fs.Length)];
                int read = fs.Read(buffer, 0, buffer.Length);
                string content = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                if (fs.Length > maxBytes)
                {
                    content += Environment.NewLine + Environment.NewLine + "… (truncated)";
                }

                _titleLabel.Text = file.Name;
                _metaLabel.Text = meta;
                _textBox.Text = content;
                _textBox.Visible = true;
                _imageBox.Visible = false;
                _emptyLabel.Visible = false;
                return;
            }
            catch
            {
                // Fall back to empty preview
            }
        }

        if (ShellPreviewHost.HasHandler(path) && IsMarkedFromInternet(path))
        {
            // Office previewers refuse files carrying Mark-of-the-Web, as in File Explorer.
            ShowEmpty(file.Name, meta, "This file came from the internet and can't be previewed. Unblock it in Properties to preview.");
            return;
        }

        ShowEmpty(file.Name, meta, "No preview available for this file type.");
    }

    /// <summary>
    /// Releases any native preview handler and forgets the last path, so the next
    /// UpdatePreview reloads even for the same selection (e.g. after the pane is re-shown).
    /// </summary>
    public void ClearPreview()
    {
        _lastPath = null;
        ClearImage();
        UnloadShellPreview();
    }

    private bool TryShowShellPreview(string path, string title, string meta)
    {
        _titleLabel.Text = title;
        _metaLabel.Text = meta;
        _handlerPanel.Visible = true;
        _handlerPanel.BringToFront();
        _imageBox.Visible = false;
        _textBox.Visible = false;
        _emptyLabel.Visible = false;

        if (_shellPreview.TryLoad(path, _handlerPanel))
        {
            return true;
        }

        _handlerPanel.Visible = false;
        return false;
    }

    private static bool IsMarkedFromInternet(string path)
    {
        try
        {
            return File.Exists(path + ":Zone.Identifier");
        }
        catch
        {
            return false;
        }
    }

    private void UnloadShellPreview()
    {
        _shellPreview.Unload();
        _handlerPanel.Visible = false;
    }

    private void ShowEmpty(string title, string meta, string message)
    {
        _titleLabel.Text = title;
        _metaLabel.Text = meta;
        _emptyLabel.Text = message;
        _emptyLabel.Visible = true;
        _imageBox.Visible = false;
        _textBox.Visible = false;
    }

    private void ClearImage()
    {
        if (_imageBox.Image is not null)
        {
            Image old = _imageBox.Image;
            _imageBox.Image = null;
            old.Dispose();
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
            ClearImage();
            _shellPreview.Dispose();
            _titleFont.Dispose();
            _metaFont.Dispose();
            _monoFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
