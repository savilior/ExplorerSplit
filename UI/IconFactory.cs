using System.Drawing.Drawing2D;
using Explorer2.Interop;

namespace Explorer2.UI;

internal static class IconFactory
{
    public static Icon CreateTrayIcon()
    {
        const int size = 32;
        using Bitmap bitmap = new(size, size);
        using Graphics g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using Icon? nativeIcon = File.Exists(explorerPath)
            ? Icon.ExtractAssociatedIcon(explorerPath)
            : null;

        if (nativeIcon is not null)
        {
            g.DrawIcon(nativeIcon, new Rectangle(0, 0, size, size));
        }
        else
        {
            using SolidBrush fallbackBrush = new(Color.FromArgb(242, 195, 76));
            g.FillRectangle(fallbackBrush, 2, 6, 28, 21);
        }

        // Aesthetic vertical split line down the center of the folder to denote Explorer2.
        const int splitX = 16;
        using (Pen glowPen = new(Color.FromArgb(215, 255, 255, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(glowPen, splitX, 8, splitX, 26);
        }
        using (Pen splitPen = new(Color.FromArgb(255, 0, 103, 192), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(splitPen, splitX, 8, splitX, 26);
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
