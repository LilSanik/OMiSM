using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace OMtoSMConverter
{
    /// <summary>
    /// Dual-Waveform Auto-Alignment for osu! to StepMania conversion.
    ///
    /// Automatically aligns osu!mania beatmap timing with the StepMania / Arrow Vortex
    /// audio waveform by comparing the two waveforms via continuous cross-correlation:
    ///   Waveform 1: StepMania audio waveform (decoded PCM audio onset envelope).
    ///   Waveform 2: osu! chart waveform (synthesized hit-object pulse train).
    /// Finds the exact millisecond time shift automatically with ZERO manual intervention.
    /// </summary>
    internal static class AudioOffsetDetector
    {
        // ─── BASS P/Invoke ──────────────────────────────────────────────────────

        private const uint BASS_STREAM_DECODE = 0x200000;
        private const uint BASS_SAMPLE_FLOAT  = 256;
        private const uint BASS_UNICODE       = 0x80000000;

        private const int SAMPLE_RATE = 44100;
        private const double READ_SECONDS = 45.0;

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr dsguid);

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern uint BASS_StreamCreateFile(bool mem, string file, ulong offset, ulong length, uint flags);

        [DllImport("bass.dll")]
        private static extern uint BASS_ChannelGetData(uint handle, [In, Out] float[] buffer, uint length);

        [DllImport("bass.dll")]
        private static extern bool BASS_StreamFree(uint handle);

        [DllImport("bass.dll")]
        private static extern int BASS_ErrorGetCode();

        // ─── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Calculate the StepMania #OFFSET value using dual-waveform auto-alignment.
        /// </summary>
        public static double CalculateSmOffset(
            string audioPath,
            double timingPointMs,
            double smStartMs,
            IEnumerable<double> hitObjectTimes = null)
        {
            double shiftMs;
            if (TryDualWaveformAlignment(audioPath, hitObjectTimes, out shiftMs))
            {
                double alignedStartMs = smStartMs + shiftMs;
                return -(alignedStartMs / 1000.0);
            }

            // Fallback: unadjusted measure 0 start
            return -(smStartMs / 1000.0);
        }

        // ─── Dual-Waveform Correlation ──────────────────────────────────────────

        private static bool TryDualWaveformAlignment(
            string audioPath,
            IEnumerable<double> hitObjectTimes,
            out double shiftMs)
        {
            shiftMs = 0.0;

            if (!File.Exists(audioPath))
                return false;

            string exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            string bassPath = Path.Combine(exeDir, "bass.dll");

            if (!File.Exists(bassPath))
                return false;

            try
            {
                BASS_Init(0, SAMPLE_RATE, 0, IntPtr.Zero, IntPtr.Zero);
                uint flags = BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT | BASS_UNICODE;
                uint handle = BASS_StreamCreateFile(false, audioPath, 0, 0, flags);

                if (handle == 0)
                    return false;

                try
                {
                    int totalSamples = (int)(READ_SECONDS * SAMPLE_RATE * 2);
                    float[] buffer = new float[totalSamples];
                    uint bytesRead = BASS_ChannelGetData(handle, buffer, (uint)(totalSamples * sizeof(float)));
                    if (bytesRead == 0 || bytesRead == uint.MaxValue)
                        return false;

                    int floatsRead = (int)(bytesRead / sizeof(float));
                    int monoSamples = floatsRead / 2;
                    float[] mono = new float[monoSamples];
                    for (int i = 0; i < monoSamples; i++)
                    {
                        int si = i * 2;
                        mono[i] = (si + 1 < floatsRead) ? (buffer[si] + buffer[si + 1]) * 0.5f : buffer[si];
                    }

                    // 1. Waveform 1: Audio onset envelope at 1000 Hz (1 ms per frame)
                    int hop = SAMPLE_RATE / 1000;
                    int nMs = Math.Min(monoSamples / hop, (int)(READ_SECONDS * 1000));
                    if (nMs < 1000)
                        return false;

                    int halfWin = Math.Max(1, (int)(0.0025 * SAMPLE_RATE));
                    float[] audioEnv = new float[nMs];
                    for (int i = 0; i < nMs; i++)
                    {
                        int center = i * hop;
                        int s = Math.Max(0, center - halfWin);
                        int e = Math.Min(monoSamples, center + halfWin);
                        if (e > s)
                        {
                            double sumSq = 0;
                            for (int j = s; j < e; j++)
                                sumSq += mono[j] * mono[j];
                            audioEnv[i] = (float)Math.Sqrt(sumSq / (e - s));
                        }
                    }

                    // Onset derivative (rectified positive)
                    float[] audioOnset = new float[nMs];
                    for (int i = 1; i < nMs; i++)
                    {
                        float diff = audioEnv[i] - audioEnv[i - 1];
                        audioOnset[i] = diff > 0 ? diff : 0f;
                    }

                    // 5ms box smoothing
                    float[] smoothOnset = new float[nMs];
                    for (int i = 0; i < nMs; i++)
                    {
                        float sum = 0;
                        int cnt = 0;
                        for (int k = -2; k <= 2; k++)
                        {
                            int idx = i + k;
                            if (idx >= 0 && idx < nMs)
                            {
                                sum += audioOnset[idx];
                                cnt++;
                            }
                        }
                        smoothOnset[i] = cnt > 0 ? sum / cnt : 0;
                    }

                    // 2. Waveform 2: osu! hit-object pulse train
                    float[] osuPulse = new float[nMs];
                    double firstHoTime = 1000.0;
                    if (hitObjectTimes != null)
                    {
                        bool hasFirst = false;
                        foreach (double t in hitObjectTimes)
                        {
                            if (!hasFirst)
                            {
                                firstHoTime = t;
                                hasFirst = true;
                            }
                            int tMs = (int)Math.Round(t);
                            if (tMs >= 0 && tMs < nMs)
                                osuPulse[tMs] += 1.0f;
                        }
                    }

                    // Convolve with 15ms exponential decay transient
                    float[] osuWaveform = new float[nMs];
                    int decayLen = 15;
                    float[] decay = new float[decayLen];
                    for (int k = 0; k < decayLen; k++)
                        decay[k] = (float)Math.Exp(-k * (4.0 / decayLen));

                    for (int i = 0; i < nMs; i++)
                    {
                        if (osuPulse[i] > 0)
                        {
                            float p = osuPulse[i];
                            for (int k = 0; k < decayLen && (i + k) < nMs; k++)
                                osuWaveform[i + k] += p * decay[k];
                        }
                    }

                    // Active section: from first note up to 30s
                    int actStart = Math.Max((int)firstHoTime, 500);
                    int actEnd = Math.Min(actStart + 30000, nMs);
                    int actLen = actEnd - actStart;
                    if (actLen <= 1000)
                        return false;

                    // Normalize sections
                    double meanA = 0, meanO = 0;
                    for (int i = 0; i < actLen; i++)
                    {
                        meanA += smoothOnset[actStart + i];
                        meanO += osuWaveform[actStart + i];
                    }
                    meanA /= actLen;
                    meanO /= actLen;

                    double varA = 0, varO = 0;
                    for (int i = 0; i < actLen; i++)
                    {
                        double diffA = smoothOnset[actStart + i] - meanA;
                        double diffO = osuWaveform[actStart + i] - meanO;
                        varA += diffA * diffA;
                        varO += diffO * diffO;
                    }
                    double stdA = Math.Sqrt(varA / actLen);
                    double stdO = Math.Sqrt(varO / actLen);

                    if (stdA < 1e-6 || stdO < 1e-6)
                        return false;

                    float[] normA = new float[actLen];
                    float[] normO = new float[actLen];
                    for (int i = 0; i < actLen; i++)
                    {
                        normA[i] = (float)((smoothOnset[actStart + i] - meanA) / stdA);
                        normO[i] = (float)((osuWaveform[actStart + i] - meanO) / stdO);
                    }

                    // Cross-correlation in [-60ms, +60ms]
                    int maxLag = 60;
                    int bestLag = 0;
                    double bestScore = double.MinValue;

                    for (int lag = -maxLag; lag <= maxLag; lag++)
                    {
                        double score = 0;
                        int terms = 0;
                        int iAStart = Math.Max(0, lag);
                        int iOStart = Math.Max(0, -lag);
                        int count = actLen - Math.Abs(lag);

                        for (int k = 0; k < count; k++)
                        {
                            score += normA[iAStart + k] * normO[iOStart + k];
                            terms++;
                        }

                        if (terms > 0)
                        {
                            score /= terms;
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestLag = lag;
                            }
                        }
                    }

                    shiftMs = bestLag;
                    return true;
                }
                finally
                {
                    BASS_StreamFree(handle);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
