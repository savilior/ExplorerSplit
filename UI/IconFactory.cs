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

        // Back folder tab (Windows 11 golden amber)
        using GraphicsPath backPath = new();
        backPath.AddArc(2, 4, 4, 4, 180, 90);
        backPath.AddLine(4, 4, 12, 4);
        backPath.AddLine(12, 4, 15, 8);
        backPath.AddLine(15, 8, 28, 8);
        backPath.AddArc(26, 8, 4, 4, 270, 90);
        backPath.AddLine(30, 10, 30, 22);
        backPath.AddLine(2, 22, 2, 6);
        backPath.CloseFigure();

        using (SolidBrush backBrush = new(Color.FromArgb(224, 158, 22)))
        {
            g.FillPath(backBrush, backPath);
        }

        // Front folder body (Windows 11 bright golden yellow gradient)
        Rectangle frontRect = new(2, 10, 28, 17);
        using (GraphicsPath frontPath = CreateRoundedRect(frontRect, 3))
        using (LinearGradientBrush frontBrush = new(
            frontRect,
            Color.FromArgb(255, 217, 102),
            Color.FromArgb(242, 182, 34),
            LinearGradientMode.Vertical))
        {
            g.FillPath(frontBrush, frontPath);
        }

        // Subtle Windows 11 bottom blue clasp/accent
        using (GraphicsPath bottomBar = CreateRoundedRect(new Rectangle(10, 25, 12, 2), 1))
        using (SolidBrush blueBrush = new(Color.FromArgb(0, 120, 212)))
        {
            g.FillPath(blueBrush, bottomBar);
        }

        // Aesthetic horizontal split line across the folder denoting ExplorerSplit
        using (Pen glowPen = new(Color.FromArgb(140, 255, 245, 200), 1f))
        {
            g.DrawLine(glowPen, 4, 19.5f, 28, 19.5f);
        }

        using (Pen splitLinePen = new(Color.FromArgb(0, 103, 192), 2.2f))
        {
            splitLinePen.StartCap = LineCap.Round;
            splitLinePen.EndCap = LineCap.Round;
            g.DrawLine(splitLinePen, 4.5f, 18f, 27.5f, 18f);
        }

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
