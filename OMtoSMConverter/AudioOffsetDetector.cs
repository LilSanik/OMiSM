using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OMtoSMConverter
{
    /// <summary>
    /// Detects the precise audio onset offset for StepMania timing correction.
    ///
    /// Replaces the hardcoded 77 ms fudge factor in the original OMiSM converter
    /// with a per-file waveform attack alignment algorithm.
    ///
    /// Algorithm (verified against Arrow Vortex Beat 0 placement):
    ///   1. Decode the MP3 to float PCM using BASS.dll (same library as osu!).
    ///   2. Locate the first timing point's position in the decoded audio.
    ///   3. Apply a 3 ms rolling average to smooth the absolute amplitude envelope.
    ///   4. Find the first sample that crosses 78% of the local maximum (attack onset).
    ///   5. Add a 10.5 ms calibration constant (perceptual onset + BASS encoder-delay
    ///      compensation, calibrated against Arrow Vortex on Camellia - Kisaragi).
    ///   6. The result is the physical beat-0 position in seconds → used as SM OFFSET.
    ///
    /// Fallback: if BASS is unavailable, returns the original OMiSM formula value.
    /// </summary>
    internal static class AudioOffsetDetector
    {
        // ─── BASS P/Invoke ──────────────────────────────────────────────────────

        private const uint BASS_STREAM_DECODE    = 0x200000;
        private const uint BASS_SAMPLE_FLOAT     = 0x100;
        private const uint BASS_UNICODE          = 0x80000000;

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr clsid);

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern uint BASS_StreamCreateFile(bool mem, string file, ulong offset, ulong length, uint flags);

        [DllImport("bass.dll")]
        private static extern uint BASS_ChannelGetData(uint handle, [Out] float[] buffer, uint length);

        [DllImport("bass.dll")]
        private static extern bool BASS_StreamFree(uint handle);

        [DllImport("bass.dll")]
        private static extern uint BASS_ErrorGetCode();

        // ─── Constants ──────────────────────────────────────────────────────────

        /// <summary>
        /// Sample rate used for BASS init and calculations.
        /// </summary>
        private const int SAMPLE_RATE = 44100;

        /// <summary>
        /// How many seconds of audio to read (must cover the first timing point).
        /// </summary>
        private const double READ_SECONDS = 15.0;

        /// <summary>
        /// Attack threshold: fraction of local max amplitude at which we declare onset.
        /// </summary>
        private const double ATTACK_THRESHOLD = 0.78;

        /// <summary>
        /// Smoothing window for amplitude envelope in seconds.
        /// </summary>
        private const double SMOOTH_WINDOW_SEC = 0.003;

        /// <summary>
        /// Calibration offset added to the detected attack sample position.
        /// Accounts for perceptual onset latency and BASS encoder-delay compensation.
        /// Calibrated on Camellia - Kisaragi (FL Studio LAME, 44100 Hz, 192 kbps)
        /// against Arrow Vortex Beat 0 visual alignment.
        /// </summary>
        private const double CALIBRATION_SEC = 0.0105;

        // ─── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Calculate the StepMania #OFFSET value (in seconds, negative) for a given
        /// osu! beatmap audio file and timing point.
        /// </summary>
        /// <param name="audioPath">Full path to the audio file (MP3/OGG).</param>
        /// <param name="timingPointMs">
        ///   osu! timing point time in milliseconds (first uninherited point).
        /// </param>
        /// <param name="smStartMs">
        ///   OMiSM smStartMS — the measure-aligned start time in milliseconds.
        ///   Used as the fallback when BASS is unavailable.
        /// </param>
        /// <returns>SM #OFFSET value in seconds (negative means audio starts before beat 0).</returns>
        public static double CalculateSmOffset(string audioPath, double timingPointMs, double smStartMs)
        {
            // Try waveform-based detection first
            double detectedOffsetSec;
            if (TryWaveformDetection(audioPath, timingPointMs, out detectedOffsetSec))
            {
                // detectedOffsetSec is -physicalBeatSec (where physical beat drops near timingPointMs).
                // StepMania's Measure 0 starts at smStartMs, which is (timingPointMs - smStartMs) ms earlier.
                // Therefore, Measure 0 occurs at: physicalBeatSec - ((timingPointMs - smStartMs) / 1000.0)
                // And SM #OFFSET is the negative of Measure 0 time.
                double timeDiffSec = (timingPointMs - smStartMs) / 1000.0;
                double physicalBeatSec = -detectedOffsetSec;
                double measure0Sec = physicalBeatSec - timeDiffSec;
                return -measure0Sec;
            }

            // Fallback: original OMiSM formula — -(smStartMs) / 1000
            // (The original had +77 which this detection replaces entirely)
            return -(smStartMs / 1000.0);
        }

        // ─── Core Detection ─────────────────────────────────────────────────────

        private static bool TryWaveformDetection(string audioPath, double timingPointMs, out double smOffsetSec)
        {
            smOffsetSec = 0.0;

            if (!File.Exists(audioPath))
                return false;

            // Find bass.dll next to the executable
            string exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            string bassPath = Path.Combine(exeDir, "bass.dll");

            if (!File.Exists(bassPath))
                return false;

            try
            {
                return TryBassDetection(audioPath, timingPointMs, out smOffsetSec);
            }
            catch (Exception ex)
            {
                // Log to debug output — non-fatal
                System.Diagnostics.Debug.WriteLine(string.Format("[AudioOffsetDetector] BASS detection failed: {0}", ex.Message));
                return false;
            }
        }

        private static bool TryBassDetection(string audioPath, double timingPointMs, out double smOffsetSec)
        {
            smOffsetSec = 0.0;

            // Initialize BASS (device -1 = no sound output, decode-only mode)
            BASS_Init(0, SAMPLE_RATE, 0, IntPtr.Zero, IntPtr.Zero);

            uint flags = BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT | BASS_UNICODE;
            uint handle = BASS_StreamCreateFile(false, audioPath, 0, 0, flags);

            if (handle == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    string.Format("[AudioOffsetDetector] BASS_StreamCreateFile failed: error {0}", BASS_ErrorGetCode()));
                return false;
            }

            try
            {
                return DecodeAndDetect(handle, timingPointMs, out smOffsetSec);
            }
            finally
            {
                BASS_StreamFree(handle);
            }
        }

        private static bool DecodeAndDetect(uint handle, double timingPointMs, out double smOffsetSec)
        {
            smOffsetSec = 0.0;

            int totalSamples = (int)(READ_SECONDS * SAMPLE_RATE * 2); // stereo
            float[] buffer = new float[totalSamples];

            uint bytesRead = BASS_ChannelGetData(handle, buffer, (uint)(totalSamples * sizeof(float)));
            if (bytesRead == 0 || bytesRead == uint.MaxValue)
                return false;

            int floatsRead = (int)(bytesRead / sizeof(float));

            // Convert stereo to mono
            int monoSamples = floatsRead / 2;
            float[] mono = new float[monoSamples];
            for (int i = 0; i < monoSamples; i++)
            {
                int si = i * 2;
                mono[i] = (si + 1 < floatsRead)
                    ? (buffer[si] + buffer[si + 1]) * 0.5f
                    : buffer[si];
            }

            // Search window: from osu timing point, up to 50 ms ahead
            double osuSec = timingPointMs / 1000.0;
            int idxStart = (int)(osuSec * SAMPLE_RATE);
            int idxEnd   = Math.Min(monoSamples, idxStart + (int)(0.050 * SAMPLE_RATE));

            if (idxStart >= monoSamples || idxStart < 0)
                return false;

            // Compute absolute amplitude in the window
            int windowLen = idxEnd - idxStart;
            float[] absAmp = new float[windowLen];
            for (int i = 0; i < windowLen; i++)
                absAmp[i] = Math.Abs(mono[idxStart + i]);

            // 3 ms rolling average (smoothing)
            int smoothWin = Math.Max(1, (int)(SMOOTH_WINDOW_SEC * SAMPLE_RATE));
            float[] smooth = RollingAverage(absAmp, smoothWin);

            // Find max
            float maxVal = 0f;
            foreach (float s in smooth)
                if (s > maxVal) maxVal = s;

            if (maxVal < 0.001f)
                return false;

            // Find first sample at or above 78% of max
            float thresh = (float)(ATTACK_THRESHOLD * maxVal);
            int attackIdx = -1;
            for (int i = 0; i < smooth.Length; i++)
            {
                if (smooth[i] >= thresh)
                {
                    attackIdx = i;
                    break;
                }
            }

            if (attackIdx < 0)
                return false;

            // Physical beat position = osu timing point + attack offset + calibration
            double physicalBeatSec = osuSec + (attackIdx / (double)SAMPLE_RATE) + CALIBRATION_SEC;

            // SM OFFSET is negative (audio starts before beat 0)
            smOffsetSec = -physicalBeatSec;

            System.Diagnostics.Debug.WriteLine(
                string.Format("[AudioOffsetDetector] Detected beat at {0:F6}s -> SM OFFSET = {1:F6}s", physicalBeatSec, smOffsetSec));

            return true;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static float[] RollingAverage(float[] input, int windowSize)
        {
            float[] output = new float[input.Length];
            double sum = 0;
            int count = 0;

            // Build initial window
            for (int i = 0; i < Math.Min(windowSize, input.Length); i++)
            {
                sum += input[i];
                count++;
            }

            for (int i = 0; i < input.Length; i++)
            {
                output[i] = (float)(sum / count);

                // Slide window
                if (i + windowSize < input.Length)
                    sum += input[i + windowSize];
                if (i - windowSize + 1 >= 0 && i + 1 < input.Length)
                    sum -= input[i - windowSize + 1];
            }

            return output;
        }
    }
}
