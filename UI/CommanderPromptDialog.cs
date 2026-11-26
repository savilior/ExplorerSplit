namespace Explorer2.UI;

/// <summary>
/// Small Total Commander style prompt: a message line, one editable text field, OK / Cancel.
/// Used for F5 Copy / F6 Move (destination folder) and F7 New Folder (folder name).
/// </summary>
internal sealed class CommanderPromptDialog : Form
{
    private readonly TextBox _input;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Configuration.AppTheme.AllowDarkForWindowTree(Handle); // dark title bar in dark mode
    }

    private readonly CheckBox? _checkBox;

    /// <param name="checkBoxText">Optional checkbox at the bottom left (F5/F6: "Don't ask again").</param>
    public CommanderPromptDialog(string title, string message, string initialText, string okText, bool isDarkMode,
        string? checkBoxText = null)
    {
        Color bg = isDarkMode ? Color.FromArgb(40, 40, 46) : Color.FromArgb(243, 243, 246);
        Color fieldBg = isDarkMode ? Color.FromArgb(28, 28, 32) : Color.White;
        Color fg = isDarkMode ? Color.FromArgb(244, 244, 248) : Color.FromArgb(22, 22, 26);
        Color buttonBg = isDarkMode ? Color.FromArgb(54, 54, 62) : Color.FromArgb(253, 253, 253);
        Color border = isDarkMode ? Color.FromArgb(70, 70, 78) : Color.FromArgb(208, 208, 214);
        Color accent = Color.FromArgb(0, 120, 212);

        Text = title;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 128);
        BackColor = bg;
        ForeColor = fg;

        Label messageLabel = new()
        {
            Text = message,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(16, 14),
            Size = new Size(488, 20),
            ForeColor = fg
        };

        _input = new TextBox
        {
            Text = initialText,
            Location = new Point(16, 40),
            Size = new Size(488, 24),
            BackColor = fieldBg,
            ForeColor = fg,
            BorderStyle = BorderStyle.FixedSingle
        };

        Button okButton = new()
        {
            Text = okText,
            DialogResult = DialogResult.OK,
            Location = new Point(316, 84),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = accent,
            ForeColor = Color.White
        };
        okButton.FlatAppearance.BorderColor = accent;

        Button cancelButton = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(414, 84),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = buttonBg,
            ForeColor = fg
        };
        cancelButton.FlatAppearance.BorderColor = border;

        Controls.Add(messageLabel);
        Controls.Add(_input);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        if (checkBoxText is not null)
        {
            _checkBox = new CheckBox
            {
                Text = checkBoxText,
                AutoSize = true,
                Location = new Point(16, 90),
                ForeColor = fg,
                BackColor = bg,
                FlatStyle = isDarkMode ? FlatStyle.Flat : FlatStyle.Standard
            };
            Controls.Add(_checkBox);
        }

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Shown += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    public string InputText => _input.Text.Trim();

    public bool IsChecked => _checkBox?.Checked == true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font.Dispose();
        }

        base.Dispose(disposing);
    }
}
