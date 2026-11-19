// Owns the DAC connection on one background thread. The UI posts requests (refresh, select preset, set gain,
// set band, save, apply a whole profile); band edits for the same band are coalesced, so dragging a handle
// sends only the latest value. State changes come back through StateChanged on the UI thread.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DacDeck
{
    internal sealed class EqState
    {
        public bool Connected;
        public DacModel Model;
        public string Firmware;
        public int Preset = -1;
        public double GlobalGain;
        public EqBand[] Bands = new EqBand[0];
        public bool Dirty;   // edited since the last save to the device
        public string Error; // a write the device did not take (sent once, with the state right after it)

        public EqState Copy()
        {
            var s = (EqState)MemberwiseClone();
            s.Bands = Bands.Select(b => b.Clone()).ToArray();
            return s;
        }
    }

    internal sealed class DeviceService : IDisposable
    {
        public event Action<EqState> StateChanged;   // raised on the UI thread
        public event Action<string, string> Notify;  // (title, subtitle) for a popup, UI thread

        private readonly SynchronizationContext _ui;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _lock = new object();
        private readonly Queue<Action<FiioDevice>> _jobs = new Queue<Action<FiioDevice>>();
        private readonly Dictionary<int, EqBand> _pendingBands = new Dictionary<int, EqBand>();
        private volatile bool _stop, _rescan = true;
        private FiioDevice _dev;
        private EqState _state = new EqState();

        public DeviceService()
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            _thread = new Thread(Loop) { IsBackground = true, Name = "DacDeck device" };
            _thread.Start();
        }

        public EqState State { get { lock (_lock) return _state.Copy(); } }

        // ------------------------------------------------------------------ requests (any thread)

        public void Rescan() { _rescan = true; _wake.Set(); }
        public void Refresh() { Enqueue(delegate(FiioDevice d) { ReadAll(d); }); }

        public void SelectPreset(int id)
        {
            Enqueue(delegate(FiioDevice d)
            {
                if (!d.SetPreset(id)) Fail("Thiết bị không nhận preset " + d.Model.PresetName(id));
                ReadAll(d);
                Raise(Notify, "EQ · " + d.Model.PresetName(id), d.Model.Name);
            });
        }

        public void SetGlobalGain(double db, bool notify)
        {
            Enqueue(delegate(FiioDevice d)
            {
                if (!d.SetGlobalGain(db)) Fail("Thiết bị không nhận gain tổng " + db.ToString("+0;-0;0") + " dB");
                var g = d.GlobalGain();
                Update(delegate(EqState s) { if (g.HasValue) s.GlobalGain = g.Value; s.Dirty = true; });
                if (notify) Raise(Notify, "Gain tổng · " + (g ?? db).ToString("+0;-0;0") + " dB", d.Model.Name);
            });
        }

        public void SetBand(EqBand b)
        {
            lock (_lock)
            {
                _pendingBands[b.Index] = b.Clone();
                if (_state.Bands.Length > b.Index) { _state.Bands[b.Index] = b.Clone(); _state.Dirty = true; }
            }
            _wake.Set();
        }

        public void Save()
        {
            Enqueue(delegate(FiioDevice d)
            {
                var preset = d.Preset();
                if (!preset.HasValue) { Fail("Không đọc được preset hiện tại"); return; }
                var p = d.Model.Preset(preset.Value);
                if (p == null || !p.Writable) { Fail("Chỉ lưu được vào USER1–3. Hãy chọn một slot USER."); return; }
                if (!d.Save(preset.Value)) { Fail("Lưu thất bại"); return; }
                Update(delegate(EqState s) { s.Dirty = false; });
                Raise(Notify, "Đã lưu vào " + p.Name, d.Model.Name);
            });
        }

        // Switches to `presetId` (a USER slot), writes gain + every band, saves
        public void ApplyProfile(int presetId, double globalGain, IList<EqBand> bands, string label)
        {
            var copy = bands.Select(b => b.Clone()).ToList();
            Enqueue(delegate(FiioDevice d)
            {
                lock (_lock) _pendingBands.Clear();
                int failed = 0;
                if (!d.SetPreset(presetId)) failed++;
                if (!d.SetGlobalGain(globalGain)) failed++;
                foreach (var b in copy) if (!d.SetBand(b)) failed++;
                if (failed == 0 && d.Save(presetId))
                {
                    Update(delegate(EqState s) { s.Dirty = false; });
                    Raise(Notify, label + " → " + d.Model.PresetName(presetId), d.Model.Name);
                }
                else Fail(string.Format("{0} lệnh không được thiết bị nhận, chưa lưu.", failed));
                ReadAll(d);
            });
        }

        private void Enqueue(Action<FiioDevice> job)
        {
            lock (_lock) _jobs.Enqueue(job);
            _wake.Set();
        }

        // ------------------------------------------------------------------ worker

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    if (_rescan)
                    {
                        _rescan = false;
                        Reconnect();
                    }
                    Action<FiioDevice> job = null;
                    EqBand band = null;
                    lock (_lock)
                    {
                        if (_jobs.Count > 0) job = _jobs.Dequeue();
                        else if (_pendingBands.Count > 0)
                        {
                            int k = _pendingBands.Keys.First();
                            band = _pendingBands[k];
                            _pendingBands.Remove(k);
                        }
                    }
                    if (_dev == null)
                    {
                        if (job != null || band != null) { lock (_lock) { _jobs.Clear(); _pendingBands.Clear(); } }
                        _wake.WaitOne(3000);
                        _rescan = true;
                        continue;
                    }
                    if (job != null) job(_dev);
                    else if (band != null)
                    {
                        if (!_dev.SetBand(band)) Fail("Thiết bị không nhận band " + (band.Index + 1) + " (preset hiện tại có thể chỉ đọc).");
                    }
                    else _wake.WaitOne(5000);
                }
                catch (Exception ex)
                {
                    Log.Error("Device", ex);
                    Disconnect();
                    Thread.Sleep(500);
                }
            }
            Disconnect();
        }

        private void Reconnect()
        {
            var all = Hid.Enumerate();
            if (_dev != null && all.Any(e => e.Path == _dev.Entry.Path)) return; // still there
            Disconnect();
            _dev = FiioDevice.Connect(all, null);
            if (_dev == null) { Publish(new EqState()); return; }
            Log.Info("Connected: " + _dev.Model.Name + " (" + _dev.Entry.Pid.ToString("X4") + ")");
            ReadAll(_dev);
            Raise(Notify, "Đã kết nối " + _dev.Model.Name, "DacDeck");
        }

        private void Disconnect()
        {
            if (_dev == null) return;
            try { _dev.Dispose(); } catch { }
            _dev = null;
            Publish(new EqState());
        }

        private void ReadAll(FiioDevice d)
        {
            var s = new EqState { Connected = true, Model = d.Model };
            s.Firmware = d.Firmware();
            s.Preset = d.Preset() ?? -1;
            s.GlobalGain = d.GlobalGain() ?? 0;
            int n = d.BandCount() ?? d.Model.Bands;
            var bands = new List<EqBand>();
            for (int i = 0; i < n; i++)
                bands.Add(d.Band(i) ?? new EqBand { Index = i, FreqHz = AutoEq.DefaultFreq(i, n), Q = 1 });
            s.Bands = bands.ToArray();
            Publish(s);
        }

        private void Fail(string msg)
        {
            Log.Info("Write failed: " + msg);
            Update(delegate(EqState s) { s.Error = msg; });
            lock (_lock) _state.Error = null; // reported once
        }

        private void Update(Action<EqState> change)
        {
            EqState snap;
            lock (_lock) { change(_state); snap = _state.Copy(); }
            Raise(snap);
        }

        private void Publish(EqState s)
        {
            lock (_lock) _state = s;
            Raise(s.Copy());
        }

        private void Raise(EqState s)
        {
            var h = StateChanged;
            if (h != null) _ui.Post(delegate { h(s); }, null);
        }

        private void Raise(Action<string, string> h, string a, string b)
        {
            if (h != null) _ui.Post(delegate { h(a, b); }, null);
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            if (_thread.IsAlive) _thread.Join(2000);
        }
    }
}
