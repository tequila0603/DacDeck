// EQ maths for the graph (biquad magnitude response, RBJ Audio EQ Cookbook) and the AutoEq / Equalizer APO
// "ParametricEQ.txt" parser.
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DacDeck
{
    internal static class EqMath
    {
        public const double SampleRate = 48000;

        // Response in dB of one band at frequency f
        public static double BandDb(EqBand b, double f)
        {
            if (Math.Abs(b.GainDb) < 1e-9) return 0;
            double A = Math.Pow(10, b.GainDb / 40.0);
            double w0 = 2 * Math.PI * b.FreqHz / SampleRate;
            double cs = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Max(0.05, b.Q));
            double sq = 2 * Math.Sqrt(A) * alpha;
            double b0, b1, b2, a0, a1, a2;
            switch (b.Type)
            {
                case EqFilter.LowShelf:
                    b0 = A * ((A + 1) - (A - 1) * cs + sq); b1 = 2 * A * ((A - 1) - (A + 1) * cs); b2 = A * ((A + 1) - (A - 1) * cs - sq);
                    a0 = (A + 1) + (A - 1) * cs + sq; a1 = -2 * ((A - 1) + (A + 1) * cs); a2 = (A + 1) + (A - 1) * cs - sq;
                    break;
                case EqFilter.HighShelf:
                    b0 = A * ((A + 1) + (A - 1) * cs + sq); b1 = -2 * A * ((A - 1) + (A + 1) * cs); b2 = A * ((A + 1) + (A - 1) * cs - sq);
                    a0 = (A + 1) - (A - 1) * cs + sq; a1 = 2 * ((A - 1) - (A + 1) * cs); a2 = (A + 1) - (A - 1) * cs - sq;
                    break;
                default:
                    b0 = 1 + alpha * A; b1 = -2 * cs; b2 = 1 - alpha * A;
                    a0 = 1 + alpha / A; a1 = -2 * cs; a2 = 1 - alpha / A;
                    break;
            }
            double w = 2 * Math.PI * f / SampleRate;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
            double dr = a0 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
            return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
        }

        public static double TotalDb(IEnumerable<EqBand> bands, double globalGain, double f)
        {
            return globalGain + bands.Sum(b => BandDb(b, f));
        }

        // Log-spaced frequency for x in [0, 1] over 20 Hz .. 20 kHz, and back
        public static double FreqAt(double x) { return 20 * Math.Pow(1000, Math.Max(0, Math.Min(1, x))); }
        public static double XOf(double f) { return Math.Log10(Math.Max(20, Math.Min(20000, f)) / 20) / 3.0; }
    }

    internal sealed class EqProfile
    {
        public double Preamp;
        public List<EqBand> Bands = new List<EqBand>();
        public List<string> Notes = new List<string>(); // what had to be changed to fit the device
    }

    internal static class AutoEq
    {
        private static readonly Regex RxPreamp = new Regex(@"^\s*Preamp\s*:\s*([-+]?\d+(?:[.,]\d+)?)\s*dB", RegexOptions.IgnoreCase);
        private static readonly Regex RxFilter = new Regex(
            @"^\s*Filter\s*\d*\s*:\s*(ON|OFF)\s+([A-Z]+)(?:\s+\d+\s*dB)?\s+Fc\s+([\d.,]+)\s*Hz(?:\s+Gain\s+([-+]?[\d.,]+)\s*dB)?(?:\s+Q\s+([\d.,]+))?",
            RegexOptions.IgnoreCase);

        private static double Num(string s) { return double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture); }

        // Parses AutoEq / Squiglink / Equalizer APO parametric EQ text and fits it to the model:
        // at most model.Bands filters (PK, LSC/LS/LSQ, HSC/HS/HSQ), gains and Q clamped to the device's range.
        public static EqProfile Parse(string text, DacModel model)
        {
            var p = new EqProfile();
            int skipped = 0;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                var mp = RxPreamp.Match(line);
                if (mp.Success) { p.Preamp = Num(mp.Groups[1].Value); continue; }
                var mf = RxFilter.Match(line);
                if (!mf.Success) continue;
                if (mf.Groups[1].Value.ToUpperInvariant() == "OFF") continue;
                string t = mf.Groups[2].Value.ToUpperInvariant();
                EqFilter type;
                if (t == "PK" || t == "PEQ" || t == "PEAK") type = EqFilter.Peak;
                else if (t == "LSC" || t == "LS" || t == "LSQ" || t == "LOWSHELF") type = EqFilter.LowShelf;
                else if (t == "HSC" || t == "HS" || t == "HSQ" || t == "HIGHSHELF") type = EqFilter.HighShelf;
                else { skipped++; continue; }
                double gain = mf.Groups[4].Success ? Num(mf.Groups[4].Value) : 0;
                double q = mf.Groups[5].Success ? Num(mf.Groups[5].Value) : 0.71;
                p.Bands.Add(new EqBand { Type = type, FreqHz = (int)Math.Round(Num(mf.Groups[3].Value)), GainDb = gain, Q = q });
            }
            if (skipped > 0) p.Notes.Add(string.Format("Bỏ qua {0} bộ lọc loại thiết bị không hỗ trợ.", skipped));
            if (p.Bands.Count > model.Bands)
            {
                p.Notes.Add(string.Format("File có {0} bộ lọc, thiết bị chỉ có {1} band: giữ {1} bộ lọc đầu.", p.Bands.Count, model.Bands));
                p.Bands = p.Bands.Take(model.Bands).ToList();
            }
            bool clamped = false;
            for (int i = 0; i < p.Bands.Count; i++)
            {
                var b = p.Bands[i];
                b.Index = i;
                double g = FiioProtocol.Clamp(b.GainDb, model.MinGain, model.MaxGain), q = FiioProtocol.Clamp(b.Q, model.MinQ, model.MaxQ);
                int f = Math.Max(20, Math.Min(20000, b.FreqHz));
                if (g != b.GainDb || q != b.Q || f != b.FreqHz) clamped = true;
                b.GainDb = Math.Round(g, 1); b.Q = Math.Round(q, 2); b.FreqHz = f;
            }
            if (clamped) p.Notes.Add("Một số giá trị vượt giới hạn thiết bị và đã được cắt về giới hạn.");
            double pre = FiioProtocol.Clamp(p.Preamp, model.MinGain, model.MaxGain);
            if (pre != p.Preamp) p.Notes.Add("Preamp vượt giới hạn và đã được cắt về giới hạn.");
            p.Preamp = Math.Floor(pre); // the firmware keeps whole dB: round down so it never gets louder
            // Unused bands become flat
            for (int i = p.Bands.Count; i < model.Bands; i++)
                p.Bands.Add(new EqBand { Index = i, Type = EqFilter.Peak, FreqHz = DefaultFreq(i, model.Bands), GainDb = 0, Q = 1 });
            return p;
        }

        // Default spread of flat bands: 31 Hz .. 16 kHz, octave-ish
        public static int DefaultFreq(int i, int count)
        {
            int[] std = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            if (count == std.Length) return std[i];
            return (int)Math.Round(EqMath.FreqAt((i + 0.5) / count));
        }
    }
}
