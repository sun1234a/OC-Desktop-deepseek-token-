using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OcAI
{
    /// <summary>
    /// Generates and plays the pet's "call" sound.
    ///
    /// The reference implementation (dsh-whale-girl-pet) synthesises its chime with
    /// WebAudio rather than shipping an audio file, so there is no asset to copy.
    /// This reproduces the same design in the plugin:
    ///
    ///   tone:    sine wave, 10 ms attack -> hold to 40% of the duration ->
    ///            exponential decay to silence
    ///   success: ascending C-major arpeggio  G5 C6 E6 G6 = 784 / 1046.5 / 1318.5 / 1568 Hz
    ///   failure: descending A-minor           A4 F4 D4 A3 = 440 / 349.2 / 293.7 / 220 Hz
    ///   notes deliberately overlap slightly so it reads as one phrase, and the
    ///   final note is longer and louder as a cadence.
    ///
    /// Playback uses winmm PlaySound (fire and forget) on a background thread, so
    /// it needs no Unity audio types and never blocks the game.
    /// </summary>
    internal static class CallSound
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_FILENAME = 0x00020000;
        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;

        private const int SAMPLE_RATE = 44100;

        /// <summary>[ frequency Hz, start delay s, duration s, peak ]</summary>
        private static readonly double[][] NOTES_OK =
        {
            new[] { 784.0,  0.00, 0.28, 0.85 },
            new[] { 1046.5, 0.11, 0.28, 0.80 },
            new[] { 1318.5, 0.22, 0.28, 0.80 },
            new[] { 1568.0, 0.33, 0.50, 0.95 },
        };

        private static readonly double[][] NOTES_FAIL =
        {
            new[] { 440.0, 0.00, 0.30, 0.80 },
            new[] { 349.2, 0.13, 0.30, 0.75 },
            new[] { 293.7, 0.26, 0.30, 0.75 },
            new[] { 220.0, 0.39, 0.50, 0.90 },
        };

        private static string _wavOk;
        private static string _wavFail;

        /// <summary>Prepares both chimes once, on first use.</summary>
        private static void EnsureWavs()
        {
            if (_wavOk != null) return;
            try
            {
                string dir = Path.GetDirectoryName(typeof(CallSound).Assembly.Location) ?? ".";
                string ok = Path.Combine(dir, "ocai_call_ok.wav");
                string fail = Path.Combine(dir, "ocai_call_fail.wav");
                if (!File.Exists(ok)) File.WriteAllBytes(ok, Render(NOTES_OK));
                if (!File.Exists(fail)) File.WriteAllBytes(fail, Render(NOTES_FAIL));
                _wavOk = ok;
                _wavFail = fail;
            }
            catch (Exception e)
            {
                Plugin.Dbg("CallSound.EnsureWavs: " + e.GetType().Name + ": " + e.Message);
                _wavOk = _wavFail = "";
            }
        }

        public static void PlayOk() { Play(_wavOk, true); }
        public static void PlayFail() { Play(_wavFail, false); }

        private static void Play(string cached, bool ok)
        {
            try
            {
                EnsureWavs();
                string path = ok ? _wavOk : _wavFail;
                if (string.IsNullOrEmpty(path)) return;

                // PlaySound with SND_ASYNC returns immediately; keep it off the
                // Unity main thread anyway so a slow disk can never hitch a frame.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT); }
                    catch { }
                });
            }
            catch (Exception e) { Plugin.Dbg("CallSound.Play: " + e.Message); }
        }

        /// <summary>Renders the note list into a 16-bit mono PCM WAV.</summary>
        private static byte[] Render(double[][] notes)
        {
            double tail = 0.05;
            double total = 0;
            foreach (var n in notes) total = Math.Max(total, n[1] + n[2]);
            total += tail;

            int count = (int)(total * SAMPLE_RATE);
            var samples = new double[count];

            foreach (var n in notes)
            {
                double freq = n[0], delay = n[1], dur = n[2], peak = n[3];
                int start = (int)(delay * SAMPLE_RATE);
                int len = (int)(dur * SAMPLE_RATE);
                int hold = (int)(dur * 0.4 * SAMPLE_RATE);

                for (int i = 0; i < len && start + i < count; i++)
                {
                    // attack 10 ms, hold to 40%, then exponential decay
                    double env;
                    int attack = (int)(0.010 * SAMPLE_RATE);
                    if (i < attack)
                    {
                        env = 0.0001 * Math.Pow(Math.Max(peak, 0.001) / 0.0001, (double)i / Math.Max(attack, 1));
                    }
                    else if (i < hold)
                    {
                        env = peak;
                    }
                    else
                    {
                        double t = (double)(i - hold) / Math.Max(len - hold, 1);
                        env = Math.Max(peak, 0.001) * Math.Pow(0.0001 / Math.Max(peak, 0.001), t);
                    }

                    double phase = 2.0 * Math.PI * freq * (i / (double)SAMPLE_RATE);
                    samples[start + i] += Math.Sin(phase) * env;
                }
            }

            // soft clip so the overlapping notes cannot wrap around
            var pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                double v = samples[i];
                if (v > 1.0) v = 1.0;
                if (v < -1.0) v = -1.0;
                short s = (short)(v * 32767.0);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            return WrapWav(pcm);
        }

        private static byte[] WrapWav(byte[] pcm)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = pcm.Length;
                int byteRate = SAMPLE_RATE * 2;

                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLen);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));

                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);              // PCM chunk size
                w.Write((short)1);        // PCM
                w.Write((short)1);        // mono
                w.Write(SAMPLE_RATE);
                w.Write(byteRate);
                w.Write((short)2);        // block align
                w.Write((short)16);       // bits per sample

                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataLen);
                w.Write(pcm);

                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
