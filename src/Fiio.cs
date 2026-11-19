// FiiO / SNOWSKY USB DACs (VID 0x2972) over HID: parametric EQ, EQ global gain, presets, save.
// Protocol: the shared FiiO HID command set as documented by fiioctl (MIT, github.com/ethanbarclay/fiioctl,
// docs/PROTOCOL.md; verified there on a KA15). Melody presets / slots: devicePEQ (0BSD, github.com/jeromeof/devicePEQ,
// usbDeviceConfig.js). Neither was verified on a Melody, so every write is read back and checked.
//   frame  = [hdr0, hdr1, 0x00, 0x00, cmd, len, data..., 0x00, 0xEE]   (replies: [... len, data..., 0xEE])
//   SET hdr = AA 0A, GET hdr = BB 0B; integers big-endian; sent as an output report, report id first.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace DacDeck
{
    internal enum EqFilter { Peak = 0, LowShelf = 1, HighShelf = 2 }

    internal sealed class EqBand
    {
        public int Index;
        public double GainDb;
        public int FreqHz;
        public double Q;
        public EqFilter Type;

        public EqBand Clone() { return (EqBand)MemberwiseClone(); }

        public bool SameAs(EqBand o)
        {
            return o != null && o.Index == Index && o.Type == Type && o.FreqHz == FreqHz
                && Math.Abs(o.GainDb - GainDb) < 0.051 && Math.Abs(o.Q - Q) < 0.0051;
        }

        public override string ToString()
        {
            return string.Format("#{0} {1} {2} Hz {3:+0.0;-0.0;0.0} dB Q {4:0.00}", Index + 1, Type, FreqHz, GainDb, Q);
        }
    }

    internal sealed class EqPreset
    {
        public int Id; public string Name; public bool Writable;
        public EqPreset(int id, string name, bool writable) { Id = id; Name = name; Writable = writable; }
    }

    // Per-model limits (from devicePEQ's model table); every FiiO model with the shared command set gets these
    internal sealed class DacModel
    {
        public string Name;
        public ushort Pid;
        public byte ReportId = 7;
        public int Bands = 10;
        public double MinGain = -12, MaxGain = 12, MinQ = 0.25, MaxQ = 8;
        public int OffPresetId = -1;
        public EqPreset[] Presets = new EqPreset[0];

        public static readonly DacModel Melody = new DacModel
        {
            Name = "SNOWSKY Melody",
            Pid = 0x0126,
            OffPresetId = 240,
            Presets = new[]
            {
                new EqPreset(0, "Jazz", false), new EqPreset(1, "Pop", false), new EqPreset(2, "Rock", false),
                new EqPreset(3, "Dance", false), new EqPreset(4, "R&B", false), new EqPreset(5, "Classic", false),
                new EqPreset(6, "Hip-hop", false),
                new EqPreset(160, "USER1", true), new EqPreset(161, "USER2", true), new EqPreset(162, "USER3", true),
            }
        };

        public static DacModel ForPid(ushort pid, string product)
        {
            if (pid == Melody.Pid) return Melody;
            // Another FiiO DAC: the shared command set, but no known preset table
            return new DacModel { Name = string.IsNullOrEmpty(product) ? string.Format("FiiO DAC ({0:X4})", pid) : product, Pid = pid };
        }

        public EqPreset Preset(int id)
        {
            return Presets.FirstOrDefault(p => p.Id == id);
        }

        public string PresetName(int id)
        {
            if (id == OffPresetId) return "Tắt EQ";
            var p = Preset(id);
            return p != null ? p.Name : "Preset " + id;
        }
    }

    internal static class FiioProtocol
    {
        public const ushort Vid = 0x2972;
        public const byte CmdFirmware = 11, CmdBand = 21, CmdPreset = 22, CmdGlobalGain = 23, CmdBandCount = 24, CmdSave = 25;

        public static byte[] Frame(bool set, byte cmd, byte[] data)
        {
            data = data ?? new byte[0];
            var f = new List<byte> { (byte)(set ? 0xAA : 0xBB), (byte)(set ? 0x0A : 0x0B), 0x00, 0x00, cmd, (byte)data.Length };
            f.AddRange(data);
            f.Add(0x00);
            f.Add(0xEE);
            return f.ToArray();
        }

        public static byte[] Report(byte reportId, byte[] frame)
        {
            var r = new byte[frame.Length + 1];
            r[0] = reportId;
            Array.Copy(frame, 0, r, 1, frame.Length);
            return r;
        }

        // A GET reply for `cmd` in an input report (report id at [0]): payload = r[7 .. 7+len]
        public static bool TryParseReply(byte[] r, byte cmd, out byte[] payload)
        {
            payload = null;
            if (r == null || r.Length < 8 || r[1] != 0xBB || r[2] != 0x0B || r[5] != cmd) return false;
            int len = r[6];
            if (7 + len > r.Length) return false;
            payload = new byte[len];
            Array.Copy(r, 7, payload, 0, len);
            return true;
        }

        public static byte[] EncodeBand(EqBand b, DacModel m)
        {
            short g = (short)Math.Round(Clamp(b.GainDb, m.MinGain, m.MaxGain) * 10.0);
            ushort f = (ushort)Math.Max(20, Math.Min(20000, b.FreqHz));
            ushort q = (ushort)Math.Round(Clamp(b.Q, m.MinQ, m.MaxQ) * 100.0);
            return new[] { (byte)b.Index, (byte)(g >> 8), (byte)g, (byte)(f >> 8), (byte)f, (byte)(q >> 8), (byte)q, (byte)b.Type };
        }

        public static EqBand DecodeBand(byte[] p)
        {
            if (p == null || p.Length < 8) return null;
            return new EqBand
            {
                Index = p[0],
                GainDb = (short)((p[1] << 8) | p[2]) / 10.0,
                FreqHz = (p[3] << 8) | p[4],
                Q = ((p[5] << 8) | p[6]) / 100.0,
                Type = p[7] <= 2 ? (EqFilter)p[7] : EqFilter.Peak
            };
        }

        public static byte[] EncodeGain(double db, DacModel m)
        {
            short v = (short)Math.Round(Clamp(db, m.MinGain, m.MaxGain) * 10.0);
            return new[] { (byte)(v >> 8), (byte)v };
        }

        public static double DecodeGain(byte[] p)
        {
            return p == null || p.Length < 2 ? double.NaN : (short)((p[0] << 8) | p[1]) / 10.0;
        }

        public static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

        public static string Hex(byte[] b, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(count, b.Length); i++) sb.Append(b[i].ToString("x2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    // An open FiiO DAC. Blocking and paced (>= 110 ms between commands, like the vendor app); use from one thread.
    internal sealed class FiioDevice : IDisposable
    {
        public readonly HidEntry Entry;
        public readonly DacModel Model;
        public Action<string> Trace; // raw TX / RX lines

        private readonly HidPort _port;
        private DateTime _next = DateTime.MinValue;

        private FiioDevice(HidEntry e, HidPort port, DacModel model) { Entry = e; _port = port; Model = model; }

        // FiiO HID interfaces able to talk both ways, vendor usage page first
        public static List<HidEntry> Candidates(List<HidEntry> all)
        {
            return all.Where(e => e.Vid == FiioProtocol.Vid && e.OutLen > 1 && e.InLen > 1)
                      .OrderByDescending(e => e.UsagePage >= 0xFF00).ThenByDescending(e => e.OutLen).ToList();
        }

        // Opens the first interface that answers the firmware query
        public static FiioDevice Connect(List<HidEntry> all, Action<string> trace)
        {
            foreach (var e in Candidates(all))
            {
                var port = Hid.Open(e);
                if (port == null) continue;
                var d = new FiioDevice(e, port, DacModel.ForPid(e.Pid, e.Product)) { Trace = trace };
                if (d.Firmware() != null) return d;
                d.Dispose();
            }
            return null;
        }

        private void Pace()
        {
            var wait = _next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            _next = DateTime.UtcNow.AddMilliseconds(110);
        }

        private bool Send(byte[] frame)
        {
            Pace();
            var report = FiioProtocol.Report(Model.ReportId, frame);
            bool ok = _port.Write(report, 500);
            if (Trace != null) Trace("TX " + FiioProtocol.Hex(report, report.Length) + (ok ? "" : "  (write failed)"));
            return ok;
        }

        public byte[] Get(byte cmd, params byte[] data)
        {
            if (!Send(FiioProtocol.Frame(false, cmd, data))) return null;
            var deadline = DateTime.UtcNow.AddMilliseconds(1000);
            while (DateTime.UtcNow < deadline)
            {
                var r = _port.Read(300);
                if (r == null) continue;
                byte[] payload;
                bool mine = FiioProtocol.TryParseReply(r, cmd, out payload);
                if (Trace != null) Trace("RX " + FiioProtocol.Hex(r, Math.Min(r.Length, 24)) + (mine ? "" : "  (other report, skipped)"));
                if (mine) return payload;
            }
            return null;
        }

        public bool Set(byte cmd, params byte[] data) { return Send(FiioProtocol.Frame(true, cmd, data)); }

        public string Firmware()
        {
            var p = Get(FiioProtocol.CmdFirmware);
            return p == null ? null : string.Join(".", p.Select(b => b.ToString()).ToArray());
        }

        public int? BandCount() { var p = Get(FiioProtocol.CmdBandCount); return p != null && p.Length > 0 ? (int?)p[0] : null; }
        public int? Preset() { var p = Get(FiioProtocol.CmdPreset); return p != null && p.Length > 0 ? (int?)p[p.Length - 1] : null; }
        public double? GlobalGain() { var p = Get(FiioProtocol.CmdGlobalGain); return p != null && p.Length >= 2 ? (double?)FiioProtocol.DecodeGain(p) : null; }
        public EqBand Band(int index) { return FiioProtocol.DecodeBand(Get(FiioProtocol.CmdBand, (byte)index)); }

        // Writes, each read back: returns false when the device does not end up with the value
        public bool SetPreset(int id)
        {
            return Set(FiioProtocol.CmdPreset, (byte)id) && Preset() == id;
        }

        public bool SetGlobalGain(double db)
        {
            if (!Set(FiioProtocol.CmdGlobalGain, FiioProtocol.EncodeGain(db, Model))) return false;
            var back = GlobalGain();
            return back.HasValue && Math.Abs(back.Value - db) < 1.0; // some firmwares keep whole dB only
        }

        public bool SetBand(EqBand b)
        {
            if (!Set(FiioProtocol.CmdBand, FiioProtocol.EncodeBand(b, Model))) return false;
            var back = Band(b.Index);
            if (back == null) return false;
            var want = FiioProtocol.DecodeBand(FiioProtocol.EncodeBand(b, Model)); // what the device can hold
            return want.SameAs(back);
        }

        public bool Save(int presetId) { return Set(FiioProtocol.CmdSave, (byte)presetId); }

        public void Dispose() { _port.Dispose(); }
    }
}
