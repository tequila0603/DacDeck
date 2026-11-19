// The EQ window: a response graph with draggable band handles (drag = frequency / gain, mouse wheel = Q),
// a table with every band's filter type, frequency, gain and Q, preset selection, EQ global gain, AutoEq import,
// save, and copy to a USER slot. Edits go to the DAC live (DeviceService coalesces them); "Lưu" stores them.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DacDeck
{
    internal sealed class EqGraph : Control
    {
        public const double RangeDb = 15;
        public event Action<EqBand> BandEdited;   // while dragging / wheeling
        public event Action<int> BandSelected;

        private EqBand[] _bands = new EqBand[0];
        private double _globalGain;
        private int _selected = -1, _dragging = -1;
        private bool _editable;
        private double _minGain = -12, _maxGain = 12, _minQ = 0.25, _maxQ = 8;

        public EqGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public void SetData(EqBand[] bands, double globalGain, bool editable, DacModel m)
        {
            _bands = bands.Select(b => b.Clone()).ToArray();
            _globalGain = globalGain;
            _editable = editable;
            if (m != null) { _minGain = m.MinGain; _maxGain = m.MaxGain; _minQ = m.MinQ; _maxQ = m.MaxQ; }
            if (_selected >= _bands.Length) _selected = -1;
            Invalidate();
        }

        public int Selected
        {
            get { return _selected; }
            set { _selected = value; Invalidate(); }
        }

        private RectangleF Plot
        {
            get { return new RectangleF(40, 12, Math.Max(10, Width - 54), Math.Max(10, Height - 36)); }
        }

        private float X(double f) { var p = Plot; return p.X + (float)EqMath.XOf(f) * p.Width; }
        private float Y(double db) { var p = Plot; return p.Y + (float)((RangeDb - db) / (2 * RangeDb)) * p.Height; }
        private double FreqOf(float x) { var p = Plot; return EqMath.FreqAt((x - p.X) / p.Width); }
        private double DbOf(float y) { var p = Plot; return RangeDb - (y - p.Y) / p.Height * 2 * RangeDb; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            using (var grid = new Pen(Theme.Grid))
            using (var strong = new Pen(Theme.GridStrong))
            using (var dim = new SolidBrush(Theme.TextDim))
            using (var small = new Font("Segoe UI", 8f))
            {
                foreach (double f in new double[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    float x = X(f);
                    g.DrawLine(grid, x, p.Top, x, p.Bottom);
                    string t = f >= 1000 ? (f / 1000) + "k" : f.ToString();
                    var sz = g.MeasureString(t, small);
                    g.DrawString(t, small, dim, Math.Min(p.Right - sz.Width, Math.Max(p.Left, x - sz.Width / 2)), p.Bottom + 3);
                }
                for (int db = -12; db <= 12; db += 6)
                {
                    float y = Y(db);
                    g.DrawLine(db == 0 ? strong : grid, p.Left, y, p.Right, y);
                    string t = db > 0 ? "+" + db : db.ToString();
                    var sz = g.MeasureString(t, small);
                    g.DrawString(t, small, dim, p.Left - sz.Width - 4, y - sz.Height / 2);
                }
            }
            if (_bands.Length == 0) return;

            // Total response, filled towards 0 dB
            var pts = new List<PointF>();
            for (int i = 0; i <= 300; i++)
            {
                double f = EqMath.FreqAt(i / 300.0);
                double db = Math.Max(-RangeDb, Math.Min(RangeDb, EqMath.TotalDb(_bands, 0, f))); // handles sit on this curve
                pts.Add(new PointF(X(f), Y(db)));
            }
            var area = new List<PointF>(pts) { new PointF(p.Right, Y(0)), new PointF(p.Left, Y(0)) };
            using (var fill = new SolidBrush(Color.FromArgb(40, Theme.Accent))) g.FillPolygon(fill, area.ToArray());
            using (var pen = new Pen(Theme.Accent, 2.2f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());

            // EQ global gain (preamp): a dashed level line with its value
            if (Math.Abs(_globalGain) > 1e-9)
            {
                float gy = Y(Math.Max(-RangeDb, Math.Min(RangeDb, _globalGain)));
                using (var pen = new Pen(Color.FromArgb(150, Theme.TextDim), 1.2f) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, p.Left, gy, p.Right, gy);
                using (var small = new Font("Segoe UI", 8f))
                using (var br = new SolidBrush(Theme.TextDim))
                {
                    string t = "Gain tổng " + (_globalGain > 0 ? "+" : "") + _globalGain.ToString("0.#") + " dB";
                    var sz = g.MeasureString(t, small);
                    g.DrawString(t, small, br, p.Right - sz.Width - 4, gy + 2);
                }
            }

            // Selected band's own curve, dashed
            if (_selected >= 0)
            {
                var sel = _bands[_selected];
                var own = new List<PointF>();
                for (int i = 0; i <= 200; i++)
                {
                    double f = EqMath.FreqAt(i / 200.0);
                    own.Add(new PointF(X(f), Y(Math.Max(-RangeDb, Math.Min(RangeDb, EqMath.BandDb(sel, f))))));
                }
                using (var pen = new Pen(Color.FromArgb(170, Theme.AccentWarm), 1.4f) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, own.ToArray());
            }

            // Handles: at (frequency, band gain), numbered
            using (var num = new Font("Segoe UI Semibold", 7.5f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i < _bands.Length; i++)
                {
                    var b = _bands[i];
                    float x = X(b.FreqHz), y = Y(Math.Max(-RangeDb, Math.Min(RangeDb, b.GainDb)));
                    bool sel = i == _selected;
                    float r = sel ? 9f : 7.5f;
                    Color c = sel ? Theme.AccentWarm : (_editable ? Theme.Accent : Theme.TextDim);
                    using (var br = new SolidBrush(c)) g.FillEllipse(br, x - r, y - r, 2 * r, 2 * r);
                    using (var rim = new Pen(Theme.Bg, 1.5f)) g.DrawEllipse(rim, x - r, y - r, 2 * r, 2 * r);
                    using (var tb = new SolidBrush(Theme.Bg)) g.DrawString((i + 1).ToString(), num, tb, new RectangleF(x - r, y - r, 2 * r, 2 * r), sf);
                }
            }
        }

        private int HandleAt(Point pt)
        {
            int best = -1; double bestD = 14 * 14;
            for (int i = 0; i < _bands.Length; i++)
            {
                double dx = X(_bands[i].FreqHz) - pt.X, dy = Y(Math.Max(-RangeDb, Math.Min(RangeDb, _bands[i].GainDb))) - pt.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = HandleAt(e.Location);
            if (i < 0) return;
            _selected = i;
            if (BandSelected != null) BandSelected(i);
            if (_editable) { _dragging = i; Capture = true; }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _dragging >= 0 || HandleAt(e.Location) >= 0 ? (_editable ? Cursors.SizeAll : Cursors.Hand) : Cursors.Default;
            if (_dragging < 0) return;
            var b = _bands[_dragging];
            b.FreqHz = (int)Math.Round(Math.Max(20, Math.Min(20000, FreqOf(e.X))));
            b.GainDb = Math.Round(Math.Max(_minGain, Math.Min(_maxGain, DbOf(e.Y))), 1);
            Invalidate();
            if (BandEdited != null) BandEdited(b.Clone());
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = -1;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_editable || _selected < 0) return;
            var b = _bands[_selected];
            b.Q = Math.Round(Math.Max(_minQ, Math.Min(_maxQ, b.Q * Math.Pow(1.12, e.Delta / 120.0))), 2);
            Invalidate();
            if (BandEdited != null) BandEdited(b.Clone());
        }
    }

    internal sealed class EqWindow : Form
    {
        private readonly DeviceService _svc;
        private EqState _state = new EqState();
        private bool _updating;

        private readonly Label _device = new Label(), _status = new Label(), _gainValue = new Label(), _hint = new Label();
        private readonly ComboBox _preset = new ComboBox(), _copyTarget = new ComboBox();
        private readonly TrackBar _gain = new TrackBar();
        private readonly EqGraph _graph = new EqGraph();
        private readonly Button _reload = new Button(), _import = new Button(), _flat = new Button(), _save = new Button(), _copy = new Button();
        private readonly List<BandRow> _rows = new List<BandRow>();
        private readonly Timer _gainDebounce = new Timer { Interval = 250 };

        private sealed class BandRow
        {
            public Label Num; public ComboBox Type; public NumericUpDown Freq, Gain, Q;
        }

        public EqWindow(DeviceService svc)
        {
            _svc = svc;
            Text = "DacDeck";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 660);
            MinimumSize = new Size(760, 560);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildLayout();
            _gainDebounce.Tick += delegate { _gainDebounce.Stop(); _svc.SetGlobalGain(_gain.Value, false); };
            ApplyState(_svc.State);
        }

        private static Button MakeButton(Button b, string text)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.GridStrong;
            b.BackColor = Theme.Panel;
            b.ForeColor = Theme.Text;
            b.AutoSize = true;
            b.Padding = new Padding(8, 2, 8, 2);
            b.Margin = new Padding(0, 0, 8, 0);
            return b;
        }

        private static ComboBox MakeCombo(ComboBox c, int width)
        {
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Panel;
            c.ForeColor = Theme.Text;
            c.Width = width;
            c.Margin = new Padding(0, 2, 8, 0);
            return c;
        }

        private static NumericUpDown MakeNum(decimal min, decimal max, decimal step, int decimals)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = 92,
                BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Left,
                Margin = new Padding(0, 1, 8, 1)
            };
        }

        private static Label MakeLabel(string text, Color color)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = color, Margin = new Padding(0, 6, 6, 0) };
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14, 10, 14, 10), BackColor = Theme.Bg };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // device + status
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // preset + gain
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // graph
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // hint
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // bands
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // actions

            var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            _device.AutoSize = true;
            _device.Font = new Font("Segoe UI Semibold", 12f);
            _device.Margin = new Padding(0, 0, 16, 0);
            _status.AutoSize = true;
            _status.ForeColor = Theme.TextDim;
            _status.Margin = new Padding(0, 5, 0, 0);
            head.Controls.Add(_device);
            head.Controls.Add(_status);
            root.Controls.Add(head, 0, 0);

            var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            bar.Controls.Add(MakeLabel("Preset", Theme.TextDim));
            bar.Controls.Add(MakeCombo(_preset, 150));
            _preset.SelectedIndexChanged += delegate
            {
                if (_updating || _preset.SelectedItem == null) return;
                _svc.SelectPreset(((PresetItem)_preset.SelectedItem).Id);
            };
            bar.Controls.Add(MakeButton(_reload, "Đọc lại"));
            _reload.Click += delegate { _svc.Refresh(); };
            bar.Controls.Add(MakeLabel("   Gain tổng", Theme.TextDim));
            _gain.Minimum = -12; _gain.Maximum = 12; _gain.TickFrequency = 3; _gain.Width = 220; _gain.AutoSize = false; _gain.Height = 30;
            _gain.BackColor = Theme.Bg;
            _gain.Scroll += delegate { _gainValue.Text = FormatDb(_gain.Value); _graph.SetData(CurrentBands(), _gain.Value, Editable, _state.Model); _gainDebounce.Stop(); _gainDebounce.Start(); };
            bar.Controls.Add(_gain);
            _gainValue.AutoSize = true; _gainValue.Margin = new Padding(0, 6, 16, 0);
            bar.Controls.Add(_gainValue);
            bar.Controls.Add(MakeLabel("Gain phần cứng: chưa hỗ trợ", Theme.TextDim));
            root.Controls.Add(bar, 0, 1);

            _graph.Dock = DockStyle.Fill;
            _graph.Margin = new Padding(0, 0, 0, 6);
            _graph.BandEdited += delegate(EqBand b) { OnBandEdited(b, true); };
            _graph.BandSelected += delegate(int i) { HighlightRow(i); };
            root.Controls.Add(_graph, 0, 2);

            _hint.AutoSize = true;
            _hint.ForeColor = Theme.AccentWarm;
            _hint.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(_hint, 0, 3);

            // Bands: two columns of rows (#, type, frequency, gain, Q)
            var bands = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
            bands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            bands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            var left = BandColumn(); var right = BandColumn();
            bands.Controls.Add(left, 0, 0);
            bands.Controls.Add(right, 1, 0);
            for (int i = 0; i < 10; i++) AddBandRow(i < 5 ? left : right, i);
            root.Controls.Add(bands, 0, 4);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.Add(MakeButton(_import, "Nhập AutoEq…"));
            _import.Click += delegate { ImportAutoEq(); };
            actions.Controls.Add(MakeButton(_flat, "Đặt phẳng"));
            _flat.Click += delegate { Flatten(); };
            actions.Controls.Add(MakeButton(_save, "Lưu vào thiết bị"));
            _save.Click += delegate { _svc.Save(); };
            actions.Controls.Add(MakeLabel("    Chép EQ này sang", Theme.TextDim));
            actions.Controls.Add(MakeCombo(_copyTarget, 90));
            actions.Controls.Add(MakeButton(_copy, "Chép"));
            _copy.Click += delegate
            {
                if (_copyTarget.SelectedItem == null) return;
                var target = (PresetItem)_copyTarget.SelectedItem;
                _svc.ApplyProfile(target.Id, _gain.Value, CurrentBands(), "Đã chép " + _state.Model.PresetName(_state.Preset));
            };
            root.Controls.Add(actions, 0, 5);

            Controls.Add(root);
        }

        private static TableLayoutPanel BandColumn()
        {
            var t = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 6, Margin = new Padding(0) };
            string[] heads = { "#", "Loại", "Tần số (Hz)", "Gain (dB)", "Q" };
            for (int c = 0; c < heads.Length; c++)
                t.Controls.Add(new Label { Text = heads[c], AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(0, 0, 8, 2) }, c, 0);
            return t;
        }

        private void AddBandRow(TableLayoutPanel t, int i)
        {
            var row = new BandRow
            {
                Num = new Label { Text = (i + 1).ToString(), AutoSize = true, Margin = new Padding(0, 5, 8, 0), ForeColor = Theme.TextDim },
                Type = MakeCombo(new ComboBox(), 104),
                Freq = MakeNum(20, 20000, 1, 0),
                Gain = MakeNum(-12, 12, 0.1m, 1),
                Q = MakeNum(0.25m, 8, 0.05m, 2)
            };
            row.Type.Items.AddRange(new object[] { "Peak", "Low shelf", "High shelf" });
            int r = i % 5 + 1; // row 0 is the header
            t.Controls.Add(row.Num, 0, r);
            t.Controls.Add(row.Type, 1, r);
            t.Controls.Add(row.Freq, 2, r);
            t.Controls.Add(row.Gain, 3, r);
            t.Controls.Add(row.Q, 4, r);
            int index = i;
            EventHandler changed = delegate { if (!_updating) OnRowEdited(index); };
            row.Type.SelectedIndexChanged += changed;
            row.Freq.ValueChanged += changed;
            row.Gain.ValueChanged += changed;
            row.Q.ValueChanged += changed;
            EventHandler focus = delegate { _graph.Selected = index; HighlightRow(index); };
            row.Type.Enter += focus; row.Freq.Enter += focus; row.Gain.Enter += focus; row.Q.Enter += focus;
            _rows.Add(row);
        }

        private sealed class PresetItem
        {
            public int Id; public string Name;
            public override string ToString() { return Name; }
        }

        private bool Editable
        {
            get
            {
                if (!_state.Connected || _state.Model == null) return false;
                var p = _state.Model.Preset(_state.Preset);
                return p != null && p.Writable;
            }
        }

        private static string FormatDb(double v) { return (v > 0 ? "+" : "") + v.ToString("0.#") + " dB"; }

        private EqBand[] CurrentBands() { return _state.Bands.Select(b => b.Clone()).ToArray(); }

        // Bands edited here in the last moments win over incoming device state (it may still be catching up)
        private readonly Dictionary<int, DateTime> _editedAt = new Dictionary<int, DateTime>();
        private static readonly TimeSpan LocalEditHold = TimeSpan.FromMilliseconds(1500);
        private string _presetSig, _copySig;
        private DateTime _errorUntil = DateTime.MinValue;

        public void ApplyState(EqState s)
        {
            var now = DateTime.UtcNow;
            if (s.Connected && _state.Connected && s.Preset == _state.Preset && s.Bands.Length == _state.Bands.Length)
                foreach (var kv in _editedAt)
                    if (now - kv.Value < LocalEditHold && kv.Key < s.Bands.Length) s.Bands[kv.Key] = _state.Bands[kv.Key].Clone();
            if (s.Preset != _state.Preset) _editedAt.Clear();

            _state = s;
            _updating = true;
            SuspendLayout();
            try
            {
                bool on = s.Connected && s.Model != null;
                SetText(_device, on ? s.Model.Name : "Chưa cắm DAC");
                string status = !string.IsNullOrEmpty(s.Error) ? "⚠ " + s.Error
                    : on ? ("Firmware " + (s.Firmware ?? "?") + (s.Loading ? "  ·  đang đọc EQ…" : "") + (s.Dirty ? "  ·  có thay đổi chưa lưu" : ""))
                    : "Cắm SNOWSKY Melody (hoặc DAC FiiO) bằng cáp USB.";
                bool error = !string.IsNullOrEmpty(s.Error);
                if (error) _errorUntil = now.AddSeconds(5); // keep a write error readable for a few seconds
                if (error || now >= _errorUntil || !on)
                {
                    SetText(_status, status);
                    _status.ForeColor = error ? Theme.Danger : (s.Dirty ? Theme.AccentWarm : Theme.TextDim);
                }

                // Preset lists: rebuilt only when they change, not on every update
                var presetItems = new List<PresetItem>();
                var copyItems = new List<PresetItem>();
                if (on)
                {
                    presetItems.AddRange(s.Model.Presets.Select(p => new PresetItem { Id = p.Id, Name = p.Name }));
                    if (s.Model.OffPresetId >= 0) presetItems.Add(new PresetItem { Id = s.Model.OffPresetId, Name = "Tắt EQ" });
                    if (s.Preset >= 0 && !presetItems.Any(i => i.Id == s.Preset)) presetItems.Add(new PresetItem { Id = s.Preset, Name = s.Model.PresetName(s.Preset) });
                    copyItems.AddRange(s.Model.Presets.Where(p => p.Writable && p.Id != s.Preset).Select(p => new PresetItem { Id = p.Id, Name = p.Name }));
                }
                string psig = string.Join("|", presetItems.Select(i => i.Id + i.Name).ToArray());
                if (psig != _presetSig) { _presetSig = psig; _preset.Items.Clear(); _preset.Items.AddRange(presetItems.Cast<object>().ToArray()); }
                var sel = _preset.Items.Cast<PresetItem>().FirstOrDefault(i => i.Id == s.Preset);
                if (_preset.SelectedItem != sel) _preset.SelectedItem = sel;
                string csig = string.Join("|", copyItems.Select(i => i.Id.ToString()).ToArray());
                if (csig != _copySig)
                {
                    _copySig = csig;
                    _copyTarget.Items.Clear();
                    _copyTarget.Items.AddRange(copyItems.Cast<object>().ToArray());
                    if (_copyTarget.Items.Count > 0) _copyTarget.SelectedIndex = 0;
                }

                if (!_gainDebounce.Enabled) // not while the user is moving the slider
                {
                    int gv = (int)Math.Max(_gain.Minimum, Math.Min(_gain.Maximum, Math.Round(s.GlobalGain)));
                    if (_gain.Value != gv) _gain.Value = gv;
                    SetText(_gainValue, on ? FormatDb(s.GlobalGain) : "");
                }

                bool edit = Editable;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    var b = i < s.Bands.Length ? s.Bands[i] : null;
                    bool has = b != null, en = has && edit;
                    if (row.Freq.Enabled != en) row.Type.Enabled = row.Freq.Enabled = row.Gain.Enabled = row.Q.Enabled = en;
                    row.Num.Enabled = has;
                    if (!has) continue;
                    if (row.Type.SelectedIndex != (int)b.Type) row.Type.SelectedIndex = (int)b.Type;
                    SetNum(row.Freq, b.FreqHz);
                    SetNum(row.Gain, (decimal)b.GainDb);
                    SetNum(row.Q, (decimal)b.Q);
                }
                _graph.SetData(s.Bands, s.GlobalGain, edit, s.Model);

                _preset.Enabled = _reload.Enabled = _gain.Enabled = on;
                _import.Enabled = on && _copyTarget.Items.Count + (edit ? 1 : 0) > 0;
                _flat.Enabled = edit;
                _save.Enabled = edit;
                _copy.Enabled = _copyTarget.Enabled = on && _copyTarget.Items.Count > 0;
                SetText(_hint, on && !edit
                    ? (s.Preset == s.Model.OffPresetId ? "EQ đang tắt. Chọn một preset để bật." : "Preset có sẵn chỉ xem được. Chọn USER1–3 để chỉnh, hoặc chép EQ này sang một slot USER.")
                    : (on ? "Kéo các điểm trên đồ thị để chỉnh tần số và gain, lăn chuột để đổi Q. Thay đổi phát ngay; bấm \"Lưu vào thiết bị\" để giữ lại." : ""));
            }
            finally
            {
                ResumeLayout();
                _updating = false;
            }
        }

        private static void SetText(Control c, string text) { if (c.Text != text) c.Text = text; }

        private static void SetNum(NumericUpDown n, decimal v)
        {
            v = Math.Max(n.Minimum, Math.Min(n.Maximum, Math.Round(v, n.DecimalPlaces)));
            if (n.Value != v) n.Value = v;
        }

        private void HighlightRow(int index)
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Num.ForeColor = i == index ? Theme.AccentWarm : Theme.TextDim;
        }

        private void OnRowEdited(int i)
        {
            if (i >= _state.Bands.Length) return;
            var row = _rows[i];
            var b = _state.Bands[i].Clone();
            b.Type = (EqFilter)Math.Max(0, row.Type.SelectedIndex);
            b.FreqHz = (int)row.Freq.Value;
            b.GainDb = (double)row.Gain.Value;
            b.Q = (double)row.Q.Value;
            OnBandEdited(b, false);
        }

        // A band changed in the graph or the table: keep both in step and send it to the DAC
        private void OnBandEdited(EqBand b, bool fromGraph)
        {
            if (!Editable || b.Index >= _state.Bands.Length) return;
            _state.Bands[b.Index] = b.Clone();
            _editedAt[b.Index] = DateTime.UtcNow;
            _state.Dirty = true;
            if (fromGraph)
            {
                _updating = true;
                try
                {
                    var row = _rows[b.Index];
                    SetNum(row.Freq, b.FreqHz);
                    SetNum(row.Gain, (decimal)b.GainDb);
                    SetNum(row.Q, (decimal)b.Q);
                }
                finally { _updating = false; }
            }
            else _graph.SetData(_state.Bands, _gain.Value, true, _state.Model);
            if (DateTime.UtcNow >= _errorUntil)
            {
                SetText(_status, "Firmware " + (_state.Firmware ?? "?") + "  ·  có thay đổi chưa lưu");
                _status.ForeColor = Theme.AccentWarm;
            }
            _svc.SetBand(b);
        }

        private void Flatten()
        {
            foreach (var b in _state.Bands.Where(b => Math.Abs(b.GainDb) > 1e-9))
            {
                var flat = b.Clone();
                flat.GainDb = 0;
                OnBandEdited(flat, false);
            }
            ApplyState(_state);
        }

        private void ImportAutoEq()
        {
            using (var dlg = new OpenFileDialog { Title = "Chọn file Parametric EQ (AutoEq / Squiglink)", Filter = "Parametric EQ (*.txt)|*.txt|Tất cả (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                EqProfile prof;
                try { prof = AutoEq.Parse(File.ReadAllText(dlg.FileName), _state.Model); }
                catch (Exception ex) { MessageBox.Show(this, "Không đọc được file:\n" + ex.Message, "DacDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (prof.Bands.All(b => Math.Abs(b.GainDb) < 1e-9))
                {
                    MessageBox.Show(this, "File không có bộ lọc nào dùng được.", "DacDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // Target: the current USER slot, or the slot picked in "Chép EQ này sang"
                int target = Editable ? _state.Preset : ((PresetItem)_copyTarget.SelectedItem).Id;
                var msg = new StringBuilder();
                msg.AppendLine(string.Format("Nạp \"{0}\" vào {1} và lưu vào thiết bị?", Path.GetFileNameWithoutExtension(dlg.FileName), _state.Model.PresetName(target)));
                msg.AppendLine();
                msg.AppendLine("Gain tổng (preamp): " + FormatDb(prof.Preamp));
                foreach (var b in prof.Bands.Where(b => Math.Abs(b.GainDb) > 1e-9)) msg.AppendLine("  " + b);
                foreach (var n in prof.Notes) { msg.AppendLine(); msg.Append("⚠ ").AppendLine(n); }
                if (MessageBox.Show(this, msg.ToString(), "Nhập AutoEq", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _svc.ApplyProfile(target, prof.Preamp, prof.Bands, "AutoEq " + Path.GetFileNameWithoutExtension(dlg.FileName));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                if (_state.Dirty && Editable)
                {
                    var r = MessageBox.Show(this, "EQ có thay đổi chưa lưu vào thiết bị. Lưu trước khi đóng?\n(Không lưu thì thay đổi sẽ mất khi rút DAC.)",
                        "DacDeck", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                    if (r == DialogResult.Yes) _svc.Save();
                }
                e.Cancel = true; // keep running in the tray
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _gainDebounce.Dispose();
            base.Dispose(disposing);
        }
    }
}
