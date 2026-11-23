using System.Diagnostics;
using System.Reflection;

namespace Explorer2.UI;

/// <summary>About Explorer2: app name, version, author and portfolio link.</summary>
internal sealed class AboutDialog : Form
{
    private const string PortfolioUrl = "https://itailiors.github.io/portfolio-hub/";

    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Bitmap? _iconBitmap;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Configuration.AppTheme.AllowDarkForWindowTree(Handle); // dark title bar in dark mode
    }

    public AboutDialog(bool isDarkMode, Icon? appIcon)
    {
        Color bg = isDarkMode ? Color.FromArgb(40, 40, 46) : Color.FromArgb(243, 243, 246);
        Color fg = isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(22, 22, 26);
        Color muted = isDarkMode ? Color.FromArgb(164, 164, 174) : Color.FromArgb(100, 100, 110);
        Color link = isDarkMode ? Color.FromArgb(96, 175, 255) : Color.FromArgb(0, 103, 192);
        Color buttonBg = isDarkMode ? Color.FromArgb(54, 54, 62) : Color.FromArgb(253, 253, 253);
        Color border = isDarkMode ? Color.FromArgb(70, 70, 78) : Color.FromArgb(208, 208, 214);

        _titleFont = new Font("Segoe UI Semibold", 14f, FontStyle.Regular);
        _bodyFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        Text = "About Explorer2";
        Font = _bodyFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 190);
        BackColor = bg;
        ForeColor = fg;
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        if (appIcon is not null)
        {
            _iconBitmap = new Icon(appIcon, 48, 48).ToBitmap();
            Controls.Add(new PictureBox
            {
                Image = _iconBitmap,
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(24, 24),
                Size = new Size(48, 48)
            });
        }

        Controls.Add(new Label
        {
            Text = "Explorer2",
            Font = _titleFont,
            ForeColor = fg,
            AutoSize = true,
            Location = new Point(88, 22)
        });

        // Set at build time from git ("1.0.15+89c6e24"): shown as "1.0.15 (89c6e24)"
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        int plus = version.IndexOf('+');
        if (plus > 0)
        {
            version = $"{version[..plus]} ({version[(plus + 1)..]})";
        }
        Controls.Add(new Label
        {
            Text = $"Version {version}",
            ForeColor = muted,
            AutoSize = true,
            Location = new Point(90, 52)
        });

        Controls.Add(new Label
        {
            Text = "Made By Savvy",
            ForeColor = fg,
            AutoSize = true,
            Location = new Point(90, 84)
        });

        LinkLabel linkLabel = new()
        {
            Text = PortfolioUrl,
            AutoSize = true,
            Location = new Point(90, 108),
            LinkColor = link,
            ActiveLinkColor = link,
            VisitedLinkColor = link,
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        linkLabel.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(PortfolioUrl) { UseShellExecute = true });
            }
            catch
            {
                // No default browser available
            }
        };
        Controls.Add(linkLabel);

        Button okButton = new()
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(286, 146),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = buttonBg,
            ForeColor = fg
        };
        okButton.FlatAppearance.BorderColor = border;
        Controls.Add(okButton);

        AcceptButton = okButton;
        CancelButton = okButton;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _iconBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }
}
