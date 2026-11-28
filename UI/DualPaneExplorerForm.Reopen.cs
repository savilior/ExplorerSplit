namespace Explorer2.UI;

/// <summary>What a window shows (place, split, tabs), so it can be reopened as it was.</summary>
internal sealed record WindowLayout(
    Rectangle Bounds,
    bool Maximized,
    bool Split,
    bool RightPaneSelected,
    IReadOnlyList<string> LeftTabs,
    int LeftActive,
    IReadOnlyList<string> RightTabs,
    int RightActive);

/// <summary>
/// Reopening a window in place: used when a setting that is applied when a window is built (the
/// theme) changes, so open windows show it at once.
/// </summary>
internal sealed partial class DualPaneExplorerForm
{
    public WindowLayout CaptureLayout()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        return new WindowLayout(
            bounds,
            WindowState == FormWindowState.Maximized,
            _isSplitEnabled,
            _selectedPane == _rightFilesPane,
            _leftFilesPane.Tabs.Select(t => t.FolderPath).ToList(),
            _leftFilesPane.ActiveTab is { } left ? _leftFilesPane.IndexOfTab(left) : 0,
            _rightFilesPane.Tabs.Select(t => t.FolderPath).ToList(),
            _rightFilesPane.ActiveTab is { } right ? _rightFilesPane.IndexOfTab(right) : 0);
    }

    /// <summary>
    /// Adds the layout's other tabs (the window was built with each pane's first tab) and selects
    /// the tabs and pane that were active.
    /// </summary>
    public void ApplyLayout(WindowLayout layout)
    {
        AddRemainingTabs(_leftFilesPane, layout.LeftTabs, layout.LeftActive);
        AddRemainingTabs(_rightFilesPane, layout.RightTabs, layout.RightActive);
        if (layout.Split && layout.RightPaneSelected)
        {
            SetSelectedPane(_rightFilesPane);
        }

        if (layout.Maximized)
        {
            MaximizedBounds = Screen.FromRectangle(layout.Bounds).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }

        _titleTabBar.Invalidate();
    }

    private static void AddRemainingTabs(TabbedExplorerPane pane, IReadOnlyList<string> paths, int activeIndex)
    {
        for (int i = 1; i < paths.Count; i++)
        {
            pane.AddNewTab(paths[i], activate: false);
        }

        if (activeIndex > 0 && activeIndex < pane.Tabs.Count)
        {
            pane.SelectTab(pane.Tabs[activeIndex], notifyPaneSelected: false);
        }
    }
}
