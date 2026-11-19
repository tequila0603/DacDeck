// Owns the DAC connection on one background thread. The UI posts requests; state comes back through
// StateChanged on the UI thread. Kept responsive on purpose:
//  - band edits are coalesced per band and written without waiting for a read-back, so dragging a handle is
//    heard right away; the written bands are read back once the edits pause (and before saving);
//  - preset / gain requests are coalesced (only the latest one runs) and shown at once, from a per-preset
//    cache when there is one, while the DAC is read in the background band by band;
//  - with no DAC connected the HID list is only rescanned on a device change (or every 15 s as a fallback).
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
        public bool Dirty;    // edited since the last save to the device
        public bool Loading;  // bands still being read from the DAC
        public string Error;  // a write the device did not take (sent once, with the state right after it)

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

        private static readonly TimeSpan VerifyAfter = TimeSpan.FromMilliseconds(700);

        private readonly SynchronizationContext _ui;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _lock = new object();
        private readonly Queue<Action<FiioDevice>> _jobs = new Queue<Action<FiioDevice>>();
        private readonly Dictionary<int, EqBand> _pendingBands = new Dictionary<int, EqBand>();
        private readonly Dictionary<int, EqBand> _unverified = new Dictionary<int, EqBand>();   // written, not read back yet
        private readonly Dictionary<int, EqState> _cache = new Dictionary<int, EqState>();       // last reading per preset
        private int? _pendingPreset;
        private double? _pendingGain;
        private DateTime _lastBandWrite = DateTime.MinValue;
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
        public void Refresh() { Enqueue(delegate(FiioDevice d) { ReadEq(d, true); }); }

        // Shown at once (cached bands when known); the DAC is switched and read in the background
        public void SelectPreset(int id)
        {
            EqState snap;
            lock (_lock)
            {
                if (!_state.Connected) return;
                _pendingPreset = id;
                _pendingBands.Clear();
                _state.Preset = id;
                _state.Dirty = false;
                EqState cached;
                if (_cache.TryGetValue(id, out cached)) { _state.Bands = cached.Bands.Select(b => b.Clone()).ToArray(); _state.GlobalGain = cached.GlobalGain; }
                _state.Loading = true;
                snap = _state.Copy();
            }
            Raise(snap);
            Raise(Notify, "EQ · " + snap.Model.PresetName(id), snap.Model.Name);
            _wake.Set();
        }

        public void SetGlobalGain(double db, bool notify)
        {
            EqState snap;
            lock (_lock)
            {
                if (!_state.Connected) return;
                _pendingGain = db;
                _state.GlobalGain = db;
                _state.Dirty = true;
                snap = _state.Copy();
            }
            Raise(snap);
            if (notify) Raise(Notify, "Gain tổng · " + db.ToString("+0;-0;0") + " dB", snap.Model.Name);
            _wake.Set();
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
                FlushEdits(d);
                if (!VerifyWritten(d)) return; // something did not stick: do not store it
                var preset = d.Preset();
                if (!preset.HasValue) { Fail("Không đọc được preset hiện tại"); return; }
                var p = d.Model.Preset(preset.Value);
                if (p == null || !p.Writable) { Fail("Chỉ lưu được vào USER1–3. Hãy chọn một slot USER."); return; }
                if (!d.Save(preset.Value)) { Fail("Lưu thất bại"); return; }
                Update(delegate(EqState s) { s.Dirty = false; });
                CacheCurrent();
                Raise(Notify, "Đã lưu vào " + p.Name, d.Model.Name);
            });
        }

        // Switches to `presetId` (a USER slot), writes gain + every band, checks them, saves
        public void ApplyProfile(int presetId, double globalGain, IList<EqBand> bands, string label)
        {
            var copy = bands.Select(b => b.Clone()).ToList();
            Enqueue(delegate(FiioDevice d)
            {
                lock (_lock) { _pendingBands.Clear(); _pendingPreset = null; _pendingGain = null; }
                int failed = 0;
                if (!d.SetPreset(presetId)) failed++;
                if (!d.SetGlobalGain(globalGain)) failed++;
                foreach (var b in copy) if (!d.SetBand(b, true)) failed++;
                if (failed == 0 && d.Save(presetId))
                    Raise(Notify, label + " → " + d.Model.PresetName(presetId), d.Model.Name);
                else
                    Fail(string.Format("{0} lệnh không được thiết bị nhận, chưa lưu.", failed));
                ReadEq(d, false);
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
                    if (_rescan) { _rescan = false; Reconnect(); }
                    if (_dev == null)
                    {
                        lock (_lock) { _jobs.Clear(); _pendingBands.Clear(); _pendingPreset = null; _pendingGain = null; }
                        if (!_wake.WaitOne(15000)) _rescan = true; // plug-ins arrive through Rescan(); this is a fallback
                        continue;
                    }
                    if (!Step(_dev)) _wake.WaitOne(_unverified.Count > 0 ? 250 : 5000);
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

        // One unit of work, most urgent first. Returns false when there was nothing to do.
        private bool Step(FiioDevice d)
        {
            Action<FiioDevice> job = null;
            int? preset = null;
            double? gain = null;
            EqBand band = null;
            lock (_lock)
            {
                if (_pendingPreset.HasValue) { preset = _pendingPreset; _pendingPreset = null; }
                else if (_pendingBands.Count > 0) { int k = _pendingBands.Keys.First(); band = _pendingBands[k]; _pendingBands.Remove(k); }
                else if (_pendingGain.HasValue) { gain = _pendingGain; _pendingGain = null; }
                else if (_jobs.Count > 0) job = _jobs.Dequeue();
            }
            if (preset.HasValue)
            {
                lock (_lock) _unverified.Clear();
                if (!d.SetPreset(preset.Value)) Fail("Thiết bị không nhận preset " + d.Model.PresetName(preset.Value));
                ReadEq(d, false);
                return true;
            }
            if (band != null)
            {
                if (!d.SetBand(band, false)) Fail("Không gửi được band " + (band.Index + 1) + " tới DAC.");
                lock (_lock) { _unverified[band.Index] = band; _lastBandWrite = DateTime.UtcNow; }
                return true;
            }
            if (gain.HasValue)
            {
                if (!d.SetGlobalGain(gain.Value)) Fail("Thiết bị không nhận gain tổng " + gain.Value.ToString("+0;-0;0") + " dB");
                return true;
            }
            if (job != null) { job(d); return true; }
            bool due;
            lock (_lock) due = _unverified.Count > 0 && DateTime.UtcNow - _lastBandWrite >= VerifyAfter;
            if (due) { VerifyWritten(d); return true; }
            return false;
        }

        // Writes what is still queued (before a save)
        private void FlushEdits(FiioDevice d)
        {
            while (true)
            {
                EqBand band = null; double? gain = null;
                lock (_lock)
                {
                    if (_pendingBands.Count > 0) { int k = _pendingBands.Keys.First(); band = _pendingBands[k]; _pendingBands.Remove(k); }
                    else if (_pendingGain.HasValue) { gain = _pendingGain; _pendingGain = null; }
                }
                if (band != null) { d.SetBand(band, false); lock (_lock) _unverified[band.Index] = band; }
                else if (gain.HasValue) d.SetGlobalGain(gain.Value);
                else return;
            }
        }

        // Reads back the bands written without verification; reports (and shows) any the DAC did not take
        private bool VerifyWritten(FiioDevice d)
        {
            List<EqBand> check;
            lock (_lock) { check = _unverified.Values.ToList(); _unverified.Clear(); }
            var bad = new List<int>();
            foreach (var b in check)
            {
                var back = d.Band(b.Index);
                if (back == null || !FiioProtocol.DecodeBand(FiioProtocol.EncodeBand(b, d.Model)).SameAs(back))
                {
                    bad.Add(b.Index + 1);
                    if (back != null) Update(delegate(EqState s) { if (s.Bands.Length > back.Index) s.Bands[back.Index] = back; });
                }
            }
            if (bad.Count > 0) Fail("DAC không giữ band " + string.Join(", ", bad.Select(i => i.ToString()).ToArray()) + " (preset này có thể chỉ đọc).");
            return bad.Count == 0;
        }

        private void Reconnect()
        {
            var all = Hid.Enumerate();
            if (_dev != null && all.Any(e => e.Path == _dev.Entry.Path)) return; // still there
            Disconnect();
            _dev = FiioDevice.Connect(all, null);
            if (_dev == null) return;
            Log.Info("Connected: " + _dev.Model.Name + " (" + _dev.Entry.Pid.ToString("X4") + ")");
            lock (_lock) _cache.Clear();
            ReadEq(_dev, true);
            Raise(Notify, "Đã kết nối " + _dev.Model.Name, "DacDeck");
        }

        private void Disconnect()
        {
            if (_dev == null) return;
            try { _dev.Dispose(); } catch { }
            _dev = null;
            lock (_lock) _unverified.Clear();
            Publish(new EqState());
        }

        // Preset and gain first (shown at once), then the bands one by one
        private void ReadEq(FiioDevice d, bool full)
        {
            EqState s;
            lock (_lock) s = _state.Copy();
            s.Connected = true;
            s.Model = d.Model;
            if (full || s.Firmware == null) s.Firmware = d.Firmware();
            s.Preset = d.Preset() ?? -1;
            s.GlobalGain = d.GlobalGain() ?? 0;
            int n = s.Bands.Length > 0 && !full ? s.Bands.Length : (d.BandCount() ?? d.Model.Bands);
            if (s.Bands.Length != n)
                s.Bands = Enumerable.Range(0, n).Select(i => new EqBand { Index = i, FreqHz = AutoEq.DefaultFreq(i, n), Q = 1 }).ToArray();
            s.Loading = true;
            s.Dirty = false;
            Publish(s);
            for (int i = 0; i < n; i++)
            {
                lock (_lock) if (_pendingPreset.HasValue) return; // the user already picked another preset
                var b = d.Band(i);
                if (b != null) Update(delegate(EqState st) { if (st.Bands.Length > b.Index) st.Bands[b.Index] = b; });
            }
            Update(delegate(EqState st) { st.Loading = false; });
            CacheCurrent();
        }

        private void CacheCurrent()
        {
            lock (_lock) if (_state.Preset >= 0) _cache[_state.Preset] = _state.Copy();
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
