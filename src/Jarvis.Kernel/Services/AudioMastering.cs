namespace Jarvis.Kernel.Services;

/// <summary>
/// Subtle broadcast-style polish for synthesized speech, pure C# on 16-bit mono PCM
/// (the kernel deliberately has no NAudio/ffmpeg dependency). Chain:
///   high-pass 80 Hz (kill rumble/DC) → high-shelf −2.5 dB @ 7.8 kHz (de-harsh) →
///   gentle 2.5:1 compression → loudness normalize to −20 dBFS RMS, peak-capped at −1.5 dBFS.
/// Tuned to be inaudible as an "effect" — it only makes every reply land at the same,
/// slightly warmer level. Toggle per profile via "mastering" in voice/profiles.json.
/// </summary>
public static class AudioMastering
{
    private const double HighPassHz = 80;
    private const double ShelfHz = 7800;
    private const double ShelfGainDb = -2.5;
    private const double CompThresholdDb = -18;
    private const double CompRatio = 2.5;
    private const double CompAttackMs = 8;
    private const double CompReleaseMs = 160;
    private const double TargetRmsDb = -20;
    private const double PeakCeilingDb = -1.5;

    public static byte[] Master(byte[] pcm16, int sampleRate)
    {
        var n = pcm16.Length / 2;
        if (n < 32) return pcm16;

        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768.0;

        Biquad.HighPass(sampleRate, HighPassHz).Process(x);
        Biquad.HighShelf(sampleRate, ShelfHz, ShelfGainDb).Process(x);
        Compress(x, sampleRate);
        NormalizeLoudness(x);

        var outBytes = new byte[pcm16.Length];
        for (var i = 0; i < n; i++)
        {
            var v = (int)Math.Round(Math.Clamp(x[i], -1.0, 1.0) * 32767.0);
            BitConverter.TryWriteBytes(outBytes.AsSpan(i * 2, 2), (short)v);
        }
        return outBytes;
    }

    /// <summary>Feed-forward compressor with a one-pole envelope follower.</summary>
    private static void Compress(double[] x, int rate)
    {
        var attack = Math.Exp(-1.0 / (CompAttackMs * 0.001 * rate));
        var release = Math.Exp(-1.0 / (CompReleaseMs * 0.001 * rate));
        var threshold = DbToLin(CompThresholdDb);
        double env = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var a = Math.Abs(x[i]);
            env = a > env ? attack * env + (1 - attack) * a : release * env + (1 - release) * a;
            if (env > threshold)
            {
                // gain so that output level = threshold + (env-threshold)/ratio
                var target = threshold * Math.Pow(env / threshold, 1.0 / CompRatio);
                x[i] *= target / env;
            }
        }
    }

    /// <summary>One static gain to hit −20 dBFS RMS over the speech (non-silent) portion,
    /// capped so the absolute peak never exceeds −1.5 dBFS. No clipping by construction.</summary>
    private static void NormalizeLoudness(double[] x)
    {
        double sum = 0, peak = 0; var counted = 0;
        foreach (var v in x)
        {
            var a = Math.Abs(v);
            if (a > peak) peak = a;
            if (a > 0.003) { sum += v * v; counted++; }   // measure speech, not the gaps
        }
        if (counted < 16 || peak <= 0) return;
        var rms = Math.Sqrt(sum / counted);
        var gain = Math.Min(DbToLin(TargetRmsDb) / rms, DbToLin(PeakCeilingDb) / peak);
        if (Math.Abs(gain - 1.0) < 0.01) return;
        for (var i = 0; i < x.Length; i++) x[i] *= gain;
    }

    private static double DbToLin(double db) => Math.Pow(10, db / 20.0);

    /// <summary>RBJ cookbook biquad, direct form 1.</summary>
    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public static Biquad HighPass(int rate, double fc, double q = 0.7071)
        {
            var w = 2 * Math.PI * fc / rate;
            var (cos, sin) = (Math.Cos(w), Math.Sin(w));
            var alpha = sin / (2 * q);
            var a0 = 1 + alpha;
            return new Biquad
            {
                _b0 = (1 + cos) / 2 / a0,
                _b1 = -(1 + cos) / a0,
                _b2 = (1 + cos) / 2 / a0,
                _a1 = -2 * cos / a0,
                _a2 = (1 - alpha) / a0,
            };
        }

        public static Biquad HighShelf(int rate, double fc, double gainDb, double slope = 0.9)
        {
            var amp = Math.Pow(10, gainDb / 40.0);
            var w = 2 * Math.PI * fc / rate;
            var (cos, sin) = (Math.Cos(w), Math.Sin(w));
            var alpha = sin / 2 * Math.Sqrt((amp + 1 / amp) * (1 / slope - 1) + 2);
            var two = 2 * Math.Sqrt(amp) * alpha;
            var a0 = (amp + 1) - (amp - 1) * cos + two;
            return new Biquad
            {
                _b0 = amp * ((amp + 1) + (amp - 1) * cos + two) / a0,
                _b1 = -2 * amp * ((amp - 1) + (amp + 1) * cos) / a0,
                _b2 = amp * ((amp + 1) + (amp - 1) * cos - two) / a0,
                _a1 = 2 * ((amp - 1) - (amp + 1) * cos) / a0,
                _a2 = ((amp + 1) - (amp - 1) * cos - two) / a0,
            };
        }

        public void Process(double[] x)
        {
            for (var i = 0; i < x.Length; i++)
            {
                var y = _b0 * x[i] + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x[i];
                _y2 = _y1; _y1 = y;
                x[i] = y;
            }
        }
    }
}
