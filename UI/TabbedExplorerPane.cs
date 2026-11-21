using Explorer2.Core;
using Explorer2.Interop;

namespace Explorer2.UI;

internal sealed class TabbedExplorerPane : Panel
{
    private readonly List<ExplorerTabInstance> _tabs = new();
    private readonly Panel _contentContainer;
    private readonly bool _isDarkMode;

    public event Action<TabbedExplorerPane>? PaneSelected;
    public event Action<TabbedExplorerPane, ExplorerTabInstance>? ActiveTabStateChanged;
    public event Action<TabbedExplorerPane>? TabsChanged;
    public event Action<TabbedExplorerPane>? LastTabCloseRequested;
    public event Action<TabbedExplorerPane, ExplorerTabInstance>? ActiveTabNavigated;

    public TabbedExplorerPane(string paneName, bool isDarkMode)
    {
        PaneName = paneName;
        _isDarkMode = isDarkMode;

        Color bg = isDarkMode ? Color.FromArgb(25, 25, 25) : Color.White;

        Dock = DockStyle.Fill;
        BackColor = bg;
        Margin = Padding.Empty;
        Padding = Padding.Empty;

        _contentContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        Controls.Add(_contentContainer);
    }

    public string PaneName { get; }
    public ExplorerTabInstance? ActiveTab { get; private set; }
    public bool IsActivePane { get; private set; }
    public IReadOnlyList<ExplorerTabInstance> Tabs => _tabs;

    public void SetIsActivePane(bool isActive)
    {
        if (IsActivePane != isActive)
        {
            IsActivePane = isActive;
            TabsChanged?.Invoke(this);
        }
    }

    public ExplorerTabInstance AddNewTab(string? initialPath = null, bool activate = true)
    {
        string targetPath = !string.IsNullOrWhiteSpace(initialPath)
            ? initialPath
            : ActiveTab?.FolderPath ?? ExplorerDetector.HomeShellGuid;

        uint viewMode = ActiveTab?.CurrentViewMode ?? ShellBrowserInterop.FVM_DETAILS;
        Color hostBg = _isDarkMode ? Color.FromArgb(25, 25, 25) : Color.White;

        ExplorerTabInstance tab = new(targetPath, hostBg, viewMode);
        tab.NavigationCompleted += OnTabNavigationCompleted;
        _tabs.Add(tab);

        if (activate || ActiveTab is null)
        {
            SelectTab(tab, notifyPaneSelected: false);
        }
        else
        {
            TabsChanged?.Invoke(this);
        }

        return tab;
    }

    public void SelectTab(ExplorerTabInstance tab, bool notifyPaneSelected = true)
    {
        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (ActiveTab != tab)
        {
            _contentContainer.SuspendLayout();
            try
            {
                if (ActiveTab is not null)
                {
                    ActiveTab.HostPanel.Visible = false;
                }

                ActiveTab = tab;
                if (!_contentContainer.Controls.Contains(tab.HostPanel))
                {
                    _contentContainer.Controls.Add(tab.HostPanel);
                }

                tab.HostPanel.Visible = true;
                tab.HostPanel.BringToFront();
                tab.EnsureInitialized();
                tab.SyncRect();
            }
            finally
            {
                _contentContainer.ResumeLayout(true);
            }
        }

        TabsChanged?.Invoke(this);

        if (notifyPaneSelected)
        {
            PaneSelected?.Invoke(this);
        }

        ActiveTabStateChanged?.Invoke(this, tab);
    }

    public void CloseTab(ExplorerTabInstance tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (_tabs.Count == 1)
        {
            LastTabCloseRequested?.Invoke(this);
            return;
        }

        bool wasActive = ActiveTab == tab;
        _tabs.RemoveAt(index);
        _contentContainer.Controls.Remove(tab.HostPanel);
        tab.NavigationCompleted -= OnTabNavigationCompleted;
        tab.Dispose();

        if (wasActive)
        {
            int nextIndex = Math.Clamp(index, 0, _tabs.Count - 1);
            SelectTab(_tabs[nextIndex], notifyPaneSelected: true);
        }
        else
        {
            TabsChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Moves every tab (including the last one) into <paramref name="targetPane"/>, appended after
    /// its existing tabs, and makes this pane's active tab the active tab there. Leaves this pane empty.
    /// </summary>
    public void MoveAllTabsTo(TabbedExplorerPane targetPane)
    {
        if (targetPane == this || _tabs.Count == 0)
        {
            return;
        }

        ExplorerTabInstance? previouslyActive = ActiveTab;
        List<ExplorerTabInstance> moving = _tabs.ToList();

        _tabs.Clear();
        ActiveTab = null;

        foreach (ExplorerTabInstance tab in moving)
        {
            _contentContainer.Controls.Remove(tab.HostPanel);
            tab.NavigationCompleted -= OnTabNavigationCompleted;
            targetPane.AdoptTab(tab);
        }

        if (previouslyActive is not null)
        {
            targetPane.SelectTab(previouslyActive, notifyPaneSelected: false);
        }

        TabsChanged?.Invoke(this);
    }

    /// <summary>
    /// Closes and disposes every tab, leaving the pane empty (used when a split pane is closed).
    /// </summary>
    public void CloseAllTabs()
    {
        List<ExplorerTabInstance> closing = _tabs.ToList();
        _tabs.Clear();
        ActiveTab = null;

        foreach (ExplorerTabInstance tab in closing)
        {
            _contentContainer.Controls.Remove(tab.HostPanel);
            tab.NavigationCompleted -= OnTabNavigationCompleted;
            tab.Dispose();
        }

        TabsChanged?.Invoke(this);
    }

    private void AdoptTab(ExplorerTabInstance tab)
    {
        tab.NavigationCompleted += OnTabNavigationCompleted;
        tab.HostPanel.Visible = false;
        _tabs.Add(tab);
        _contentContainer.Controls.Add(tab.HostPanel);
    }

    public void CloseActiveTab()
    {
        if (ActiveTab is not null)
        {
            CloseTab(ActiveTab);
        }
    }

    public int IndexOfTab(ExplorerTabInstance tab) => _tabs.IndexOf(tab);

    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _tabs.Count ||
            toIndex < 0 || toIndex >= _tabs.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        ExplorerTabInstance item = _tabs[fromIndex];
        _tabs.RemoveAt(fromIndex);
        _tabs.Insert(toIndex, item);
        TabsChanged?.Invoke(this);
    }

    public void CycleTab(bool forward)
    {
        if (_tabs.Count <= 1 || ActiveTab is null)
        {
            return;
        }

        int currentIndex = _tabs.IndexOf(ActiveTab);
        if (currentIndex < 0)
        {
            return;
        }

        int nextIndex = forward
            ? (currentIndex + 1) % _tabs.Count
            : (currentIndex - 1 + _tabs.Count) % _tabs.Count;

        SelectTab(_tabs[nextIndex], notifyPaneSelected: true);
    }

    public bool TryTransferTabTo(ExplorerTabInstance tab, TabbedExplorerPane targetPane, int insertIndex)
    {
        if (targetPane == this || _tabs.Count <= 1)
        {
            return false;
        }

        int oldIndex = _tabs.IndexOf(tab);
        if (oldIndex < 0)
        {
            return false;
        }

        bool wasActive = ActiveTab == tab;
        _tabs.RemoveAt(oldIndex);
        _contentContainer.Controls.Remove(tab.HostPanel);
        tab.NavigationCompleted -= OnTabNavigationCompleted;

        if (wasActive)
        {
            int nextIdx = Math.Clamp(oldIndex, 0, _tabs.Count - 1);
            SelectTab(_tabs[nextIdx], notifyPaneSelected: false);
        }
        else
        {
            TabsChanged?.Invoke(this);
        }

        targetPane.AcceptTransferredTab(tab, insertIndex);
        return true;
    }

    private void AcceptTransferredTab(ExplorerTabInstance tab, int insertIndex)
    {
        tab.NavigationCompleted += OnTabNavigationCompleted;
        int clampedIndex = Math.Clamp(insertIndex, 0, _tabs.Count);
        _tabs.Insert(clampedIndex, tab);
        SelectTab(tab, notifyPaneSelected: true);
    }

    private void OnTabNavigationCompleted(ExplorerTabInstance tab)
    {
        if (tab == ActiveTab)
        {
            ActiveTabNavigated?.Invoke(this, tab);
        }

        TabsChanged?.Invoke(this);
        if (tab == ActiveTab)
        {
            ActiveTabStateChanged?.Invoke(this, tab);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (ExplorerTabInstance tab in _tabs)
            {
                tab.NavigationCompleted -= OnTabNavigationCompleted;
                tab.Dispose();
            }

            _tabs.Clear();
        }

        base.Dispose(disposing);
    }
}
