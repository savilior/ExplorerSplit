using System.Drawing.Drawing2D;
using ExplorerSplit.Interop;

namespace ExplorerSplit.UI;

internal static class IconFactory
{
    public static Icon CreateTrayIcon()
    {
        const int size = 32;
        using Bitmap bitmap = new(size, size);
        using Graphics g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Outer rounded dark-slate frame
        using GraphicsPath outerPath = CreateRoundedRect(new Rectangle(2, 4, 28, 24), 5);
        using SolidBrush bgBrush = new(Color.FromArgb(24, 90, 189));
        g.FillPath(bgBrush, outerPath);

        // Left pane
        using GraphicsPath leftPane = CreateRoundedRect(new Rectangle(5, 7, 10, 18), 2);
        using SolidBrush paneBrush = new(Color.FromArgb(245, 248, 255));
        g.FillPath(paneBrush, leftPane);

        // Right pane
        using GraphicsPath rightPane = CreateRoundedRect(new Rectangle(17, 7, 10, 18), 2);
        using SolidBrush rightPaneBrush = new(Color.FromArgb(205, 224, 252));
        g.FillPath(rightPaneBrush, rightPane);

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    internal static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = Math.Max(1, radius * 2);
        Size size = new(diameter, diameter);
        Rectangle arc = new(bounds.Location, size);

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();

        return path;
    }
}
