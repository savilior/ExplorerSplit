using Explorer2.Core;

namespace Explorer2.UI;

/// <summary>
/// Turns each of the navigation pane's top rows (Home, Gallery, Recycle Bin, Desktop, Downloads,
/// Documents) on or off. The rows are listed in their current order; they are reordered by dragging
/// them in the navigation pane.
/// </summary>
internal sealed class TopSectionDialog : Form
{
    private readonly CheckedListBox _list;
    private readonly List<TopSection.Item> _items;

    public TopSectionDialog()
    {
        Text = "Navigation Pane Top Section";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(320, 268);

        Label prompt = new()
        {
            Text = "Rows shown at the top of the navigation pane. Drag them in the navigation pane to change their order.",
            Location = new Point(16, 14),
            Size = new Size(288, 40)
        };

        _items = TopSection.All.ToList();
        _list = new CheckedListBox
        {
            Location = new Point(16, 60),
            Size = new Size(288, 150),
            CheckOnClick = true,
            IntegralHeight = false
        };
        foreach (TopSection.Item item in _items)
        {
            _list.Items.Add(item.Name, !TopSection.IsHidden(item.Id));
        }

        Button okButton = new()
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(142, 226),
            Size = new Size(76, 28)
        };

        Button cancelButton = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(228, 226),
            Size = new Size(76, 28)
        };

        Controls.AddRange([prompt, _list, okButton, cancelButton]);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>Ids of the rows left unchecked.</summary>
    public IEnumerable<string> HiddenIds =>
        _items.Where((_, i) => !_list.GetItemChecked(i)).Select(item => item.Id).ToList();
}
