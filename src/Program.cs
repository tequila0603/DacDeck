// DacDeck: tray app for FiiO / SNOWSKY USB DACs (SNOWSKY Melody first): EQ presets, EQ global gain,
// a full parametric EQ window and AutoEq import.
//   DacDeck.exe              tray icon + EQ window
//   DacDeck.exe --minimized  tray icon only (used when starting with Windows)
//   DacDeck.exe --probe      read-only check of the connected DAC, printed to the console
//   DacDeck.exe --check      self-check
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DacDeck")]
[assembly: AssemblyProduct("DacDeck")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace DacDeck
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Func<string, bool> has = delegate(string flag) { return args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)); };

            if (has("--check") || has("--probe"))
            {
                Native.AttachConsole(-1);
                Console.WriteLine();
                if (has("--check")) { SelfCheck.Run(); Console.WriteLine("Self-check passed."); }
                if (has("--probe")) Probe.Run(delegate(string line) { Console.WriteLine(line); Log.Info("PROBE " + line); });
                return 0;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Error("UI", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                if (ex != null) Log.Error("Fatal", ex);
            };

            bool createdNew;
            using (new Mutex(true, @"Local\DacDeck_SingleInstance", out createdNew))
            {
                if (!createdNew) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(!has("--minimized")));
            }
            return 0;
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private readonly NotifyIcon _icon;
        private readonly DeviceService _svc;
        private readonly Toast _toast;
        private readonly DeviceWatcher _watcher;
        private EqWindow _window;
        private EqState _state = new EqState();

        public TrayApp(bool openWindow)
        {
            _svc = new DeviceService();
            _toast = new Toast();
            _icon = new NotifyIcon { Text = "DacDeck", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
            try { _icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { _icon.Icon = SystemIcons.Application; }
            _icon.ContextMenuStrip.Opening += delegate { BuildMenu(_icon.ContextMenuStrip); };
            _icon.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };

            _svc.StateChanged += OnState;
            _svc.Notify += delegate(string title, string sub)
            {
                float scale = _window != null && _window.IsHandleCreated ? _window.DeviceDpiScale() : DpiScale();
                _toast.Show(title, sub, Theme.SystemUsesLightTheme(), scale);
            };
            _watcher = new DeviceWatcher(delegate { _svc.Rescan(); });
            if (openWindow) ShowWindow();
        }

        private static float DpiScale()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        private void OnState(EqState s)
        {
            _state = s;
            _icon.Text = s.Connected && s.Model != null
                ? Truncate("DacDeck · " + s.Model.Name + " · " + s.Model.PresetName(s.Preset), 63)
                : "DacDeck · chưa cắm DAC";
            if (_window != null && !_window.IsDisposed) _window.ApplyState(s);
        }

        private static string Truncate(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }

        private void ShowWindow()
        {
            if (_window == null || _window.IsDisposed) _window = new EqWindow(_svc);
            _window.ApplyState(_svc.State);
            _window.Show();
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
        }

        private void BuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var s = _state;
            bool on = s.Connected && s.Model != null;
            menu.Items.Add(new ToolStripMenuItem(on ? ("DacDeck · " + s.Model.Name + (s.Firmware != null ? " (FW " + s.Firmware + ")" : "")) : "DacDeck · chưa cắm DAC") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            if (on)
            {
                var presets = new ToolStripMenuItem("Preset EQ");
                foreach (var p in s.Model.Presets)
                {
                    int id = p.Id;
                    presets.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, delegate { _svc.SelectPreset(id); }) { Checked = s.Preset == id });
                }
                if (s.Model.OffPresetId >= 0)
                {
                    int off = s.Model.OffPresetId;
                    presets.DropDownItems.Add(new ToolStripSeparator());
                    presets.DropDownItems.Add(new ToolStripMenuItem("Tắt EQ", null, delegate { _svc.SelectPreset(off); }) { Checked = s.Preset == off });
                }
                menu.Items.Add(presets);

                var gain = new ToolStripMenuItem("Gain tổng (" + (s.GlobalGain > 0 ? "+" : "") + s.GlobalGain.ToString("0") + " dB)");
                for (int db = 6; db >= -12; db--)
                {
                    int v = db;
                    gain.DropDownItems.Add(new ToolStripMenuItem((v > 0 ? "+" : "") + v + " dB", null, delegate { _svc.SetGlobalGain(v, true); })
                    { Checked = Math.Round(s.GlobalGain) == v });
                }
                menu.Items.Add(gain);
                menu.Items.Add(new ToolStripMenuItem("Gain phần cứng: chưa hỗ trợ") { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add(new ToolStripMenuItem("Mở cửa sổ EQ…", null, delegate { ShowWindow(); }) { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(new ToolStripMenuItem("Tìm lại DAC", null, delegate { _svc.Rescan(); }));
            menu.Items.Add(new ToolStripMenuItem("Khởi động cùng Windows", null, delegate { SetAutoStart(!AutoStart()); }) { Checked = AutoStart() });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Thoát", null, delegate { Exit(); }));
        }

        private static bool AutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("DacDeck") != null;
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("DacDeck", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("DacDeck", false);
                }
            }
            catch (Exception ex) { Log.Error("AutoStart", ex); }
        }

        private void Exit()
        {
            if (_window != null && !_window.IsDisposed && _state.Dirty)
            {
                var r = MessageBox.Show(_window, "EQ có thay đổi chưa lưu vào thiết bị. Lưu trước khi thoát?", "DacDeck",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) { _svc.Save(); Thread.Sleep(600); }
            }
            _icon.Visible = false;
            _icon.Dispose();
            _watcher.Dispose();
            _svc.Dispose();
            _toast.Close();
            if (_window != null) _window.Dispose();
            ExitThread();
        }
    }

    // WM_DEVICECHANGE -> callback (a DAC plugged in or out)
    internal sealed class DeviceWatcher : NativeWindow, IDisposable
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private readonly Action _onChange;
        private readonly System.Windows.Forms.Timer _debounce = new System.Windows.Forms.Timer { Interval = 700 };

        public DeviceWatcher(Action onChange)
        {
            _onChange = onChange;
            _debounce.Tick += delegate { _debounce.Stop(); _onChange(); };
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE) { _debounce.Stop(); _debounce.Start(); } // a plug fires a burst; act once it settles
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            _debounce.Dispose();
            DestroyHandle();
        }
    }

    internal static class FormExtensions
    {
        public static float DeviceDpiScale(this Form f)
        {
            using (var g = f.CreateGraphics()) return g.DpiX / 96f;
        }
    }

    // "DacDeck.exe --probe": read-only. Lists FiiO HID interfaces and reads firmware, bands, preset and gain.
    internal static class Probe
    {
        public static void Run(Action<string> print)
        {
            var all = Hid.Enumerate();
            var fiio = all.Where(e => e.Vid == FiioProtocol.Vid).ToList();
            print(string.Format("FiiO HID interfaces (VID 2972): {0}", fiio.Count));
            foreach (var e in fiio)
                print(string.Format("  PID {0:X4} page {1:X4} usage {2:X4} in {3} out {4} feat {5} \"{6}\"", e.Pid, e.UsagePage, e.Usage, e.InLen, e.OutLen, e.FeatLen, e.Product));
            if (fiio.Count == 0) { print("No FiiO / SNOWSKY DAC found. Is it plugged in by USB, and is the FiiO Control web page closed?"); return; }

            foreach (var e in FiioDevice.Candidates(all))
            {
                print(string.Format("Trying PID {0:X4} page {1:X4}...", e.Pid, e.UsagePage));
                using (var d = FiioDevice.Connect(new System.Collections.Generic.List<HidEntry> { e }, delegate(string s) { print("  " + s); }))
                {
                    if (d == null) { print("  no answer"); continue; }
                    d.Trace = null;
                    print("  Model:       " + d.Model.Name);
                    print("  Firmware:    " + d.Firmware());
                    var n = d.BandCount();
                    print("  Bands:       " + (n.HasValue ? n.Value.ToString() : "?"));
                    var pr = d.Preset();
                    print("  Preset:      " + (pr.HasValue ? pr.Value + " (" + d.Model.PresetName(pr.Value) + ")" : "?"));
                    var gg = d.GlobalGain();
                    print("  Global gain: " + (gg.HasValue ? gg.Value.ToString("+0.0;-0.0;0.0") + " dB" : "?"));
                    for (int i = 0; i < (n ?? d.Model.Bands); i++)
                    {
                        var b = d.Band(i);
                        print("  " + (b != null ? b.ToString() : string.Format("#{0} ?", i + 1)));
                    }
                    print("OK");
                    return;
                }
            }
            print("The DAC did not answer on any interface.");
        }
    }
}
