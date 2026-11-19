// "DacDeck.exe --check": protocol bytes against the documented examples, AutoEq parsing, EQ maths, popup motion.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Drawing;
using System.Linq;

namespace DacDeck
{
    internal static class SelfCheck
    {
        private static void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("ASSERT FAILED: " + msg);
        }

        private static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        public static void Run()
        {
            var m = DacModel.Melody;

            // 1. Frames: fiioctl README band write (band 2, 9140 Hz, +5.6 dB, Q 3.46, peak) and firmware reply
            var band = new EqBand { Index = 2, GainDb = 5.6, FreqHz = 9140, Q = 3.46, Type = EqFilter.Peak };
            var tx = FiioProtocol.Report(7, FiioProtocol.Frame(true, FiioProtocol.CmdBand, FiioProtocol.EncodeBand(band, m)));
            Assert(FiioProtocol.Hex(tx, tx.Length) == "07 aa 0a 00 00 15 08 02 00 38 23 b4 01 5a 00 00 ee", "Band frame: " + FiioProtocol.Hex(tx, tx.Length));
            Assert(FiioProtocol.DecodeBand(FiioProtocol.EncodeBand(band, m)).SameAs(band), "Band round trip");
            var neg = FiioProtocol.DecodeBand(FiioProtocol.EncodeBand(new EqBand { Index = 0, GainDb = -7.5, FreqHz = 105, Q = 0.7, Type = EqFilter.LowShelf }, m));
            Assert(Near(neg.GainDb, -7.5, 1e-9) && neg.Type == EqFilter.LowShelf, "Negative gain");
            var clamped = FiioProtocol.DecodeBand(FiioProtocol.EncodeBand(new EqBand { Index = 1, GainDb = 20, FreqHz = 5, Q = 30 }, m));
            Assert(Near(clamped.GainDb, 12, 1e-9) && clamped.FreqHz == 20 && Near(clamped.Q, 8, 1e-9), "Band clamped to the model");
            var rx = new byte[] { 0x07, 0xbb, 0x0b, 0x00, 0x22, 0x0b, 0x03, 0x01, 0x01, 0x06, 0xee, 0x55 };
            byte[] payload;
            Assert(FiioProtocol.TryParseReply(rx, FiioProtocol.CmdFirmware, out payload) && payload.SequenceEqual(new byte[] { 1, 1, 6 }), "Firmware reply");
            Assert(!FiioProtocol.TryParseReply(rx, FiioProtocol.CmdBand, out payload), "Other replies skipped");
            Assert(FiioProtocol.Hex(FiioProtocol.Frame(false, FiioProtocol.CmdBand, new byte[] { 4 }), 9) == "bb 0b 00 00 15 01 04 00 ee", "GET frame");
            Assert(Near(FiioProtocol.DecodeGain(FiioProtocol.EncodeGain(-6, m)), -6, 1e-9), "Global gain");

            // 2. Melody presets
            Assert(m.Pid == 0x0126 && m.Presets.Count(p => p.Writable) == 3 && m.Preset(160).Name == "USER1" && !m.Preset(0).Writable, "Melody presets");
            Assert(m.PresetName(240) == "Tắt EQ" && m.PresetName(7) == "Preset 7", "Preset names");

            // 3. EQ maths: a peak gives its gain at its frequency and ~0 far away; shelves reach their gain at the ends
            var pk = new EqBand { GainDb = 6, FreqHz = 1000, Q = 1.41, Type = EqFilter.Peak };
            Assert(Near(EqMath.BandDb(pk, 1000), 6, 0.05) && Math.Abs(EqMath.BandDb(pk, 20)) < 0.1, "Peak response");
            var ls = new EqBand { GainDb = -4, FreqHz = 120, Q = 0.71, Type = EqFilter.LowShelf };
            Assert(Near(EqMath.BandDb(ls, 20), -4, 0.3) && Math.Abs(EqMath.BandDb(ls, 10000)) < 0.1, "Low shelf response");
            var hs = new EqBand { GainDb = 3, FreqHz = 8000, Q = 0.71, Type = EqFilter.HighShelf };
            Assert(Near(EqMath.BandDb(hs, 20000), 3, 0.4) && Math.Abs(EqMath.BandDb(hs, 100)) < 0.1, "High shelf response");
            Assert(Near(EqMath.FreqAt(EqMath.XOf(1234)), 1234, 0.5) && Near(EqMath.FreqAt(0), 20, 1e-9) && Near(EqMath.FreqAt(1), 20000, 1e-6), "Log axis");

            // 4. AutoEq: preamp rounded down, filter types, OFF lines ignored, too many filters cut, values clamped
            string text = "Preamp: -6.4 dB\r\n" +
                          "Filter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70\n" +
                          "Filter 2: ON PK Fc 200 Hz Gain -2,6 dB Q 0.65\n" +
                          "Filter 3: OFF PK Fc 300 Hz Gain 9.0 dB Q 1.00\n" +
                          "Filter 4: ON HSC Fc 10000 Hz Gain -13.0 dB Q 0.70\n" +
                          "Filter 5: ON LS Fc 80 Hz Gain 1.0 dB\n" +
                          "Filter 6: ON BP Fc 500 Hz Gain 1.0 dB Q 1.0\n";
            for (int i = 7; i <= 14; i++) text += "Filter " + i + ": ON PK Fc " + (i * 1000) + " Hz Gain 1.0 dB Q 2.00\n";
            var prof = AutoEq.Parse(text, m);
            Assert(prof.Preamp == -7, "AutoEq preamp rounded down: " + prof.Preamp);
            Assert(prof.Bands.Count == 10 && prof.Bands[0].Type == EqFilter.LowShelf && prof.Bands[0].FreqHz == 105 && Near(prof.Bands[0].GainDb, 5.5, 1e-9), "AutoEq low shelf");
            Assert(Near(prof.Bands[1].GainDb, -2.6, 1e-9) && prof.Bands[2].Type == EqFilter.HighShelf && Near(prof.Bands[2].GainDb, -12, 1e-9), "AutoEq comma decimal + clamp");
            Assert(Near(prof.Bands[3].Q, 0.71, 1e-9) && prof.Bands[3].FreqHz == 80, "AutoEq shelf without Q");
            Assert(prof.Bands.Select(b => b.Index).SequenceEqual(Enumerable.Range(0, 10)), "AutoEq band indexes");
            Assert(prof.Notes.Count == 3, "AutoEq notes (skipped type, too many, clamped): " + prof.Notes.Count);
            var few = AutoEq.Parse("Preamp: -2 dB\nFilter 1: ON PK Fc 1000 Hz Gain 3 dB Q 1\n", m);
            Assert(few.Bands.Count == 10 && few.Bands.Skip(1).All(b => b.GainDb == 0) && few.Notes.Count == 0, "AutoEq pads flat bands");

            // 5. Popup motion and rendering
            float y; byte a; bool done;
            ToastMotion.At(0, out y, out a, out done);
            Assert(Near(y, ToastMotion.Rise, 0.01) && a <= 5 && !done, "Toast starts low");
            ToastMotion.At(ToastMotion.EnterMs + 300, out y, out a, out done);
            Assert(y == 0 && a == 255 && !done, "Toast holds");
            ToastMotion.At(ToastMotion.TotalMs, out y, out a, out done);
            Assert(done, "Toast done");
            using (var t = Toast.Render("EQ · USER1", "SNOWSKY Melody", false, 1.25f))
            using (var f1 = Toast.ComposeFrame(t, 3f, 1.25f))
            using (var f2 = Toast.ComposeFrame(t, 3.5f, 1.25f))
            {
                Assert(t.Width == 325 && t.Height == 65, "Toast scales with DPI");
                int diff = 0;
                for (int yy = 0; yy < f1.Height; yy += 1)
                    for (int xx = 0; xx < f1.Width; xx += 3)
                        if (f1.GetPixel(xx, yy) != f2.GetPixel(xx, yy)) diff++;
                Assert(diff > 50, "Toast moves sub-pixel");
            }
        }
    }
}
