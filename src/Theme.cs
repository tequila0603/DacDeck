// Colours and shared drawing (EQ glyph, rounded rectangles), Windows theme detection, and the log file.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DacDeck
{
    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(94, 196, 255);       // curve, handles, popup border
        public static readonly Color AccentWarm = Color.FromArgb(255, 184, 77);   // selected band, unsaved changes
        public static readonly Color Bg = Color.FromArgb(28, 29, 33);
        public static readonly Color Panel = Color.FromArgb(36, 38, 43);
        public static readonly Color Grid = Color.FromArgb(52, 55, 62);
        public static readonly Color GridStrong = Color.FromArgb(76, 80, 90);
        public static readonly Color Text = Color.FromArgb(236, 238, 242);
        public static readonly Color TextDim = Color.FromArgb(150, 156, 168);
        public static readonly Color Danger = Color.FromArgb(248, 113, 113);

        public static bool SystemUsesLightTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Three EQ sliders with knobs at different heights
        public static void DrawEqGlyph(Graphics g, RectangleF box, Color accent, bool light)
        {
            Color track = light ? Color.FromArgb(120, 126, 136) : Color.FromArgb(150, 156, 168);
            Color rim = light ? Color.FromArgb(24, 24, 28) : Color.FromArgb(242, 242, 244);
            float[] knob = { 0.62f, 0.30f, 0.52f };
            float step = box.Width / 3f, r = box.Width * 0.13f;
            using (var tp = new Pen(track, Math.Max(1.6f, box.Width * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var kb = new SolidBrush(accent))
            using (var kp = new Pen(rim, Math.Max(1.2f, box.Width * 0.05f)))
            {
                for (int i = 0; i < 3; i++)
                {
                    float x = box.X + step * (i + 0.5f);
                    g.DrawLine(tp, x, box.Y + r, x, box.Bottom - r);
                    float y = box.Y + r + (box.Height - 2 * r) * knob[i];
                    g.FillEllipse(kb, x - r, y - r, 2 * r, 2 * r);
                    g.DrawEllipse(kp, x - r, y - r, 2 * r, 2 * r);
                }
            }
        }
    }

    // %LOCALAPPDATA%\DacDeck\DacDeck.log (rotates at 256 KB)
    internal static class Log
    {
        private static readonly object _lock = new object();

        public static string Path
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DacDeck", "DacDeck.log"); }
        }

        public static void Info(string msg)
        {
            try
            {
                lock (_lock)
                {
                    string p = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 256 * 1024) File.Copy(p, p + ".1", true);
                    if (fi.Exists && fi.Length > 256 * 1024) File.Delete(p);
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Error(string context, Exception ex) { Info("[" + context + "] " + ex); }
    }
}
