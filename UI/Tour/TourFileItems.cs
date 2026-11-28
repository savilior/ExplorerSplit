using System.Windows.Automation;

namespace Explorer2.UI;

/// <summary>
/// Where an item of a file list is on screen, read with UI Automation (the shell's file list draws
/// its items itself, so they have no windows). Read on a worker thread: the list answers through
/// the window's own thread, which must stay free.
/// </summary>
internal static class TourFileItems
{
    public static Task<Rectangle> FindAsync(IntPtr fileList, string name) => Task.Run(() =>
    {
        try
        {
            AutomationElement list = AutomationElement.FromHandle(fileList);
            // The name as shown: without its extension when Windows hides known extensions
            AutomationElement? item = null;
            foreach (string shown in new[] { name, Path.GetFileNameWithoutExtension(name) }.Distinct())
            {
                item ??= list.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, shown)));
            }

            if (item is null)
            {
                return Rectangle.Empty;
            }

            System.Windows.Rect r = item.Current.BoundingRectangle;
            return r.IsEmpty ? Rectangle.Empty : new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }
        catch
        {
            return Rectangle.Empty;
        }
    });
}
