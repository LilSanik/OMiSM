using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace OMtoSMConverter
{
    /// <summary>
    /// Calcula el offset de StepMania comparando los waveforms de dos motores de audio:
    ///   Motor 1: BASS.dll  (como usa osu!)
    ///   Motor 2: ffmpeg    (como usa StepMania / Arrow Vortex)
    ///
    /// Decodifica el mismo MP3 con ambos motores, cross-correlaciona los waveforms crudos
    /// y la diferencia de muestras = corrección exacta del offset. Sin constantes ni heurísticas.
    /// </summary>
    internal static class AudioOffsetDetector
    {
        // ─── BASS P/Invoke ──────────────────────────────────────────────────────

        private const uint BASS_STREAM_DECODE = 0x200000;
        private const uint BASS_SAMPLE_FLOAT  = 256;
        private const uint BASS_UNICODE       = 0x80000000;
        private const int  SAMPLE_RATE        = 44100;
        private const double ANALYSIS_SECONDS = 10.0;

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

        // ─── API Pública ─────────────────────────────────────────────────────────

        /// <summary>
        /// Calcula el #OFFSET de StepMania comparando waveforms de BASS vs ffmpeg.
        /// </summary>
        /// <param name="audioPath">Ruta al archivo de audio (MP3/OGG).</param>
        /// <param name="smStartMs">Tiempo de inicio de la Medida 0 en ms (calculado por OMiSM).</param>
        /// <returns>#OFFSET en segundos (negativo).</returns>
        public static double CalculateSmOffset(string audioPath, double smStartMs)
        {
            double lagMs;
            if (TryGetEngineLag(audioPath, out lagMs))
            {
                // lagMs = cuántos ms el motor ffmpeg (StepMania) está desfasado respecto a BASS (osu!)
                // Si BASS produce audio X ms antes que ffmpeg, el offset de SM se debe ajustar en +X ms
                double correctedStartMs = smStartMs + lagMs;
                return -(correctedStartMs / 1000.0);
            }

            // Fallback: sin corrección
            return -(smStartMs / 1000.0);
        }

        // ─── Implementación ─────────────────────────────────────────────────────

        /// <summary>
        /// Decodifica el audio con BASS y con ffmpeg, cross-correlaciona los waveforms,
        /// y devuelve el lag en ms (positivo = ffmpeg empieza más tarde que BASS).
        /// </summary>
        private static bool TryGetEngineLag(string audioPath, out double lagMs)
        {
            lagMs = 0.0;

            float[] bassAudio, ffmpegAudio;

            if (!TryDecodeBASS(audioPath, out bassAudio))
            {
                System.Diagnostics.Debug.WriteLine("[AudioOffsetDetector] No se pudo decodificar con BASS.");
                return false;
            }

            if (!TryDecodeFFmpeg(audioPath, out ffmpegAudio))
            {
                System.Diagnostics.Debug.WriteLine("[AudioOffsetDetector] No se pudo decodificar con ffmpeg.");
                return false;
            }

            int lag = CrossCorrelate(bassAudio, ffmpegAudio);
            lagMs = (lag / (double)SAMPLE_RATE) * 1000.0;

            System.Diagnostics.Debug.WriteLine(
                string.Format("[AudioOffsetDetector] BASS vs ffmpeg lag: {0} muestras = {1:F3} ms", lag, lagMs));

            return true;
        }

        /// <summary>
        /// Decodifica el audio con BASS (motor de osu!) en mono float32 a 44100 Hz.
        /// </summary>
        private static bool TryDecodeBASS(string audioPath, out float[] mono)
        {
            mono = null;

            string exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            string bassPath = Path.Combine(exeDir, "bass.dll");

            if (!File.Exists(bassPath) || !File.Exists(audioPath))
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
                    int totalSamples = (int)(ANALYSIS_SECONDS * SAMPLE_RATE * 2); // estéreo
                    float[] buffer = new float[totalSamples];
                    uint bytesRead = BASS_ChannelGetData(handle, buffer, (uint)(totalSamples * sizeof(float)));
                    if (bytesRead == 0 || bytesRead == uint.MaxValue)
                        return false;

                    int floatsRead = (int)(bytesRead / sizeof(float));
                    int monoSamples = floatsRead / 2;
                    mono = new float[monoSamples];
                    for (int i = 0; i < monoSamples; i++)
                    {
                        int si = i * 2;
                        mono[i] = (si + 1 < floatsRead)
                            ? (buffer[si] + buffer[si + 1]) * 0.5f
                            : buffer[si];
                    }
                    return true;
                }
                finally
                {
                    BASS_StreamFree(handle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioOffsetDetector] Error BASS: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Decodifica el audio con ffmpeg (motor de StepMania / Arrow Vortex) en mono float32 a 44100 Hz.
        /// </summary>
        private static bool TryDecodeFFmpeg(string audioPath, out float[] mono)
        {
            mono = null;

            // Buscar ffmpeg.exe en ubicaciones comunes
            string[] candidates = {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                    "ffmpeg.exe"),
                "ffmpeg.exe" // En PATH
            };

            string ffmpegPath = null;
            foreach (string c in candidates)
            {
                try
                {
                    if (File.Exists(c)) { ffmpegPath = c; break; }
                }
                catch { }
            }

            // Intentar desde PATH si no se encontró en rutas fijas
            if (ffmpegPath == null)
                ffmpegPath = "ffmpeg";

            try
            {
                int totalSamples = (int)(ANALYSIS_SECONDS * SAMPLE_RATE);
                byte[] rawBytes = new byte[totalSamples * 4]; // float32 = 4 bytes
                int totalRead = 0;

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffmpegPath;
                psi.Arguments = string.Format(
                    "-y -i \"{0}\" -t {1} -ar {2} -ac 1 -f f32le -",
                    audioPath,
                    ANALYSIS_SECONDS.ToString("F1", CultureInfo.InvariantCulture),
                    SAMPLE_RATE);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return false;

                    // Leer stdout binario
                    using (Stream stdout = proc.StandardOutput.BaseStream)
                    {
                        int read;
                        while (totalRead < rawBytes.Length &&
                               (read = stdout.Read(rawBytes, totalRead, rawBytes.Length - totalRead)) > 0)
                        {
                            totalRead += read;
                        }
                    }

                    proc.WaitForExit(5000);
                }

                if (totalRead < 1000)
                    return false;

                int floatCount = totalRead / 4;
                mono = new float[floatCount];
                Buffer.BlockCopy(rawBytes, 0, mono, 0, floatCount * 4);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioOffsetDetector] Error ffmpeg: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Cross-correlación entre dos waveforms mono en el rango [-200ms, +200ms].
        /// Devuelve el lag en muestras (positivo = b empieza más tarde que a).
        /// </summary>
        private static int CrossCorrelate(float[] a, float[] b)
        {
            int maxLag = (int)(0.200 * SAMPLE_RATE); // 200 ms en muestras
            int len = Math.Min(a.Length, b.Length);

            int bestLag = 0;
            double bestScore = double.MinValue;

            for (int lag = -maxLag; lag <= maxLag; lag++)
            {
                double score = 0;
                int iStart = Math.Max(0, lag);
                int jStart = Math.Max(0, -lag);
                int count = len - Math.Abs(lag);

                if (count <= 0) continue;

                for (int k = 0; k < count; k++)
                    score += a[iStart + k] * b[jStart + k];

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLag = lag;
                }
            }

            return bestLag;
        }
    }
}
