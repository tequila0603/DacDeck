// Popup above the tray (same motion as PinBar's): the window stays put and a background thread draws every frame,
// paced by the display (DwmFlush), with the toast placed at a fractional y inside a taller frame.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DacDeck
{
    internal static class ToastMotion
    {
        public const double EnterMs = 320, HoldMs = 1700, ExitMs = 300;
        public const double TotalMs = EnterMs + HoldMs + ExitMs;
        public const float Rise = 26f, Drop = 16f;

        public static void At(double ms, out float yOffset, out byte alpha, out bool done)
        {
            done = ms >= TotalMs;
            if (ms < 0) ms = 0;
            if (ms < EnterMs)
            {
                double t = ms / EnterMs;
                const double c1 = 1.4, c3 = c1 + 1.0; // back-out: a couple of px past the target, then settles
                double ease = 1.0 + c3 * Math.Pow(t - 1.0, 3) + c1 * Math.Pow(t - 1.0, 2);
                yOffset = (float)(Rise * (1.0 - ease));
                alpha = (byte)Math.Max(1, Math.Min(255, (int)Math.Round(255 * Math.Min(1.0, t * 2.0))));
            }
            else if (ms < EnterMs + HoldMs)
            {
                yOffset = 0f;
                alpha = 255;
            }
            else
            {
                double t = Math.Min(1.0, (ms - EnterMs - HoldMs) / ExitMs);
                yOffset = (float)(Drop * t * t * t);
                alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(255 * (1.0 - t * t))));
            }
        }
    }

    internal static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string cls, string title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    }

    // A layered window's pixels in one DIB section for the window's lifetime (no GetHbitmap per frame)
    internal sealed class LayeredSurface : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Native.POINT pptDst,
            ref Native.SIZE psize, IntPtr hdcSrc, ref Native.POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        private IntPtr _dc, _dib, _oldBmp, _bits;
        private int _w, _h;

        public void Upload(Bitmap frame)
        {
            int w = frame.Width, h = frame.Height;
            if (_dib == IntPtr.Zero || w != _w || h != _h)
            {
                Release();
                var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                _dc = CreateCompatibleDC(IntPtr.Zero);
                _dib = CreateDIBSection(_dc, ref bi, 0, out _bits, IntPtr.Zero, 0);
                _oldBmp = SelectObject(_dc, _dib);
                _w = w; _h = h;
            }
            var data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(_bits, y * w * 4), row.Length);
                }
            }
            finally { frame.UnlockBits(data); }
        }

        public bool Present(IntPtr hwnd, Point pos, byte alpha)
        {
            if (_dib == IntPtr.Zero) return false;
            var dst = new Native.POINT { x = pos.X, y = pos.Y };
            var sz = new Native.SIZE { cx = _w, cy = _h };
            var src = new Native.POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = alpha, AlphaFormat = 1 };
            return UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref sz, _dc, ref src, 0, ref blend, 2);
        }

        private void Release()
        {
            if (_dc != IntPtr.Zero && _oldBmp != IntPtr.Zero) SelectObject(_dc, _oldBmp);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            if (_dc != IntPtr.Zero) DeleteDC(_dc);
            _dc = _dib = _oldBmp = _bits = IntPtr.Zero;
        }

        public void Dispose() { Release(); }
    }

    internal sealed class Toast : Form
    {
        public const int PadTop = 8, PadBottom = 36;

        private sealed class Job { public string Title, Subtitle; public bool Light; public float Scale; }

        private readonly object _lock = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stop;
        private Job _pending;
        private readonly IntPtr _hwnd;

        public Toast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            CreateHandle();
            _hwnd = Handle;
            _thread = new Thread(AnimLoop) { IsBackground = true, Name = "Toast", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void Show(string title, string subtitle, bool lightTheme, float scale)
        {
            lock (_lock) _pending = new Job { Title = title, Subtitle = subtitle, Light = lightTheme, Scale = scale };
            _wake.Set();
        }

        // 260 x 52 (x scale): an EQ glyph, a title and a subtitle on a rounded card
        public static Bitmap Render(string title, string subtitle, bool light, float scale)
        {
            int w = (int)Math.Round(260 * scale), h = (int)Math.Round(52 * scale);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                g.ScaleTransform(scale, scale);
                Color accent = Theme.Accent;
                Color bg = light ? Color.FromArgb(246, 248, 248, 250) : Color.FromArgb(244, 32, 33, 38);
                Color fg = light ? Color.FromArgb(22, 22, 26) : Color.FromArgb(245, 245, 247);
                Color sub = light ? Color.FromArgb(70, 76, 86) : Color.FromArgb(160, 168, 180);
                using (var p = Theme.RoundRect(new RectangleF(1, 1, 258, 50), 12))
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(120, accent), 1.3f)) g.DrawPath(pen, p);
                }
                Theme.DrawEqGlyph(g, new RectangleF(12, 12, 28, 28), accent, light);
                using (var f1 = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel))
                using (var f2 = new Font("Segoe UI", 11.2f, GraphicsUnit.Pixel))
                using (var b1 = new SolidBrush(fg))
                using (var b2 = new SolidBrush(sub))
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    g.DrawString(title, f1, b1, new RectangleF(50, 8, 200, 19), sf);
                    g.DrawString(subtitle, f2, b2, new RectangleF(50, 27, 200, 17), sf);
                }
            }
            return bmp;
        }

        internal static Bitmap ComposeFrame(Bitmap toast, float yOffset, float scale)
        {
            int padTop = (int)Math.Round(PadTop * scale), padBottom = (int)Math.Round(PadBottom * scale);
            var frame = new Bitmap(toast.Width, toast.Height + padTop + padBottom, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(toast, new RectangleF(0f, padTop + yOffset * scale, toast.Width, toast.Height));
            }
            return frame;
        }

        // Centred over the notification area, resting 10 px above the taskbar
        private static Point Place(Size toast, float scale)
        {
            var bounds = Screen.PrimaryScreen.Bounds;
            var work = Screen.PrimaryScreen.WorkingArea;
            int tbTop = (bounds.Bottom - work.Bottom >= 24) ? work.Bottom : bounds.Bottom - 48;
            int cx = bounds.Right - 160;
            try
            {
                var tb = Native.FindWindow("Shell_TrayWnd", null);
                var tn = tb == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                Native.RECT r;
                if (tn != IntPtr.Zero && Native.GetWindowRect(tn, out r) && r.R > r.L) cx = r.L + Math.Min(220, r.R - r.L) / 2;
            }
            catch { }
            int x = Math.Max(bounds.Left + 8, Math.Min(bounds.Right - toast.Width - 8, cx - toast.Width / 2));
            return new Point(x, tbTop - toast.Height - (int)Math.Round((10 + PadTop) * scale));
        }

        private void AnimLoop()
        {
            Job job = null;
            Bitmap toast = null;
            bool shown = false;
            float lastY = float.NaN;
            Point pos = Point.Empty;
            var clock = new System.Diagnostics.Stopwatch();
            using (var surface = new LayeredSurface())
            {
                while (!_stop)
                {
                    try
                    {
                        Job next;
                        lock (_lock) { next = _pending; _pending = null; }
                        if (next != null)
                        {
                            job = next;
                            if (toast != null) toast.Dispose();
                            toast = Render(job.Title, job.Subtitle, job.Light, job.Scale);
                            lastY = float.NaN;
                            pos = Place(toast.Size, job.Scale);
                            clock.Restart();
                        }
                        if (job == null) { _wake.WaitOne(); continue; }

                        float y; byte alpha; bool done;
                        ToastMotion.At(clock.Elapsed.TotalMilliseconds, out y, out alpha, out done);
                        if (done)
                        {
                            Native.ShowWindowAsync(_hwnd, Native.SW_HIDE);
                            shown = false; job = null;
                            toast.Dispose(); toast = null;
                            continue;
                        }
                        bool changed = y != lastY;
                        if (changed)
                        {
                            using (var frame = ComposeFrame(toast, y, job.Scale)) surface.Upload(frame);
                            lastY = y;
                        }
                        if (changed || alpha < 255 || !shown)
                        {
                            surface.Present(_hwnd, pos, alpha);
                            if (!shown)
                            {
                                Native.ShowWindowAsync(_hwnd, Native.SW_SHOWNOACTIVATE);
                                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
                                shown = true;
                            }
                            if (Native.DwmFlush() != 0) Thread.Sleep(8);
                        }
                        else _wake.WaitOne(15);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Toast", ex);
                        job = null;
                        if (toast != null) { toast.Dispose(); toast = null; }
                        Thread.Sleep(50);
                    }
                }
                if (toast != null) toast.Dispose();
            }
        }

        private void StopAnim()
        {
            if (_stop) return;
            _stop = true;
            _wake.Set();
            if (_thread.IsAlive) _thread.Join(500);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            StopAnim();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopAnim(); _wake.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
