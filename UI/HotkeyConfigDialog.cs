using Explorer2.Configuration;
using Explorer2.Interop;

namespace Explorer2.UI;

internal sealed class HotkeyConfigDialog : Form
{
    private readonly CheckBox _ctrlCheckBox;
    private readonly CheckBox _shiftCheckBox;
    private readonly CheckBox _altCheckBox;
    private readonly TextBox _keyTextBox;

    public uint SelectedModifiers { get; private set; }
    public Keys SelectedKey { get; private set; }

    public HotkeyConfigDialog(uint currentModifiers, Keys currentKey)
    {
        SelectedModifiers = currentModifiers;
        SelectedKey = currentKey;

        Text = "Configure Split View Hotkey";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 175);

        Label promptLabel = new()
        {
            Text = "Press a key combination or choose modifiers below (active when File Explorer is focused):",
            Location = new Point(16, 14),
            Size = new Size(328, 34)
        };

        _ctrlCheckBox = new CheckBox
        {
            Text = "Ctrl",
            AutoSize = true,
            Location = new Point(18, 56),
            Checked = (currentModifiers & NativeMethods.MOD_CONTROL) != 0
        };

        _shiftCheckBox = new CheckBox
        {
            Text = "Shift",
            AutoSize = true,
            Location = new Point(84, 56),
            Checked = (currentModifiers & NativeMethods.MOD_SHIFT) != 0
        };

        _altCheckBox = new CheckBox
        {
            Text = "Alt",
            AutoSize = true,
            Location = new Point(156, 56),
            Checked = (currentModifiers & NativeMethods.MOD_ALT) != 0
        };

        _ctrlCheckBox.CheckedChanged += OnModifierCheckChanged;
        _shiftCheckBox.CheckedChanged += OnModifierCheckChanged;
        _altCheckBox.CheckedChanged += OnModifierCheckChanged;

        _keyTextBox = new TextBox
        {
            Location = new Point(16, 88),
            Size = new Size(328, 26),
            ReadOnly = true,
            TextAlign = HorizontalAlignment.Center,
            BackColor = SystemColors.Window,
            Text = AppSettings.FormatHotkey(SelectedModifiers, SelectedKey)
        };
        _keyTextBox.KeyDown += OnHotkeyTextBoxKeyDown;

        Button resetButton = new()
        {
            Text = "Reset Default",
            Location = new Point(16, 130),
            Size = new Size(100, 28)
        };
        resetButton.Click += (_, _) =>
        {
            SelectedModifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
            SelectedKey = Keys.S;
            SyncModifierCheckBoxes();
            UpdatePreview();
        };

        Button okButton = new()
        {
            Text = "OK",
            Location = new Point(182, 130),
            Size = new Size(76, 28)
        };
        okButton.Click += (_, _) =>
        {
            if (SelectedModifiers == 0 || SelectedKey == Keys.None)
            {
                MessageBox.Show(
                    this,
                    "Please select at least one modifier key (Ctrl, Shift, or Alt) and a primary key.",
                    "Explorer2",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        Button cancelButton = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(268, 130),
            Size = new Size(76, 28)
        };

        Controls.AddRange([
            promptLabel,
            _ctrlCheckBox,
            _shiftCheckBox,
            _altCheckBox,
            _keyTextBox,
            resetButton,
            okButton,
            cancelButton
        ]);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void OnModifierCheckChanged(object? sender, EventArgs e)
    {
        uint modifiers = 0;
        if (_ctrlCheckBox.Checked) modifiers |= NativeMethods.MOD_CONTROL;
        if (_shiftCheckBox.Checked) modifiers |= NativeMethods.MOD_SHIFT;
        if (_altCheckBox.Checked) modifiers |= NativeMethods.MOD_ALT;
        SelectedModifiers = modifiers;
        UpdatePreview();
    }

    private void OnHotkeyTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        Keys keyCode = e.KeyCode;
        if (keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return;
        }

        uint modifiers = 0;
        if (e.Control) modifiers |= NativeMethods.MOD_CONTROL;
        if (e.Shift) modifiers |= NativeMethods.MOD_SHIFT;
        if (e.Alt) modifiers |= NativeMethods.MOD_ALT;

        if (modifiers == 0)
        {
            return;
        }

        SelectedModifiers = modifiers;
        SelectedKey = keyCode;
        SyncModifierCheckBoxes();
        UpdatePreview();
    }

    private void SyncModifierCheckBoxes()
    {
        _ctrlCheckBox.CheckedChanged -= OnModifierCheckChanged;
        _shiftCheckBox.CheckedChanged -= OnModifierCheckChanged;
        _altCheckBox.CheckedChanged -= OnModifierCheckChanged;

        _ctrlCheckBox.Checked = (SelectedModifiers & NativeMethods.MOD_CONTROL) != 0;
        _shiftCheckBox.Checked = (SelectedModifiers & NativeMethods.MOD_SHIFT) != 0;
        _altCheckBox.Checked = (SelectedModifiers & NativeMethods.MOD_ALT) != 0;

        _ctrlCheckBox.CheckedChanged += OnModifierCheckChanged;
        _shiftCheckBox.CheckedChanged += OnModifierCheckChanged;
        _altCheckBox.CheckedChanged += OnModifierCheckChanged;
    }

    private void UpdatePreview()
    {
        _keyTextBox.Text = SelectedModifiers == 0
            ? "(Select a modifier + key)"
            : AppSettings.FormatHotkey(SelectedModifiers, SelectedKey);
    }
}
