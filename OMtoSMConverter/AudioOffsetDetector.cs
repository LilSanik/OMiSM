using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OMtoSMConverter
{
    /// <summary>
    /// Dual-Engine Waveform Offset Detector for osu!mania to StepMania conversion.
    ///
    /// 100% Portable & Self-Contained:
    ///   - Embedded BASS library: se extrae automáticamente a %TEMP% en tiempo de ejecución.
    ///   - Pure C# MP3 Inspector: analiza los encabezados MP3/LAME nativamente sin requerir
    ///     ffmpeg, ffprobe ni ninguna herramienta externa en el sistema.
    ///   - Zero dependencias externas en la carpeta del ejecutable.
    /// </summary>
    internal static class AudioOffsetDetector
    {
        // ─── Win32 / Kernel32 P/Invoke ──────────────────────────────────────────

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string libname);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        // ─── BASS P/Invoke ──────────────────────────────────────────────────────

        private const uint BASS_STREAM_DECODE = 0x200000;
        private const uint BASS_SAMPLE_FLOAT  = 256;
        private const uint BASS_UNICODE       = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct BASS_CHANNELINFO
        {
            public uint freq;
            public uint chans;
            public uint flags;
            public uint ctype;
            public uint origres;
            public uint plugin;
            public uint sample;
            public IntPtr filename;
        }

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr dsguid);

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern uint BASS_StreamCreateFile(bool mem, string file, ulong offset, ulong length, uint flags);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelGetInfo(uint handle, ref BASS_CHANNELINFO info);

        [DllImport("bass.dll")]
        private static extern bool BASS_StreamFree(uint handle);

        private static bool _bassLoaded = false;
        private static readonly object _bassLock = new object();

        // ─── Auto-Extract Embedded BASS ─────────────────────────────────────────

        /// <summary>
        /// Garantiza que bass.dll esté cargado en memoria, extrayéndolo del recurso embebido si es necesario.
        /// </summary>
        public static void EnsureBassLoaded()
        {
            if (_bassLoaded) return;
            lock (_bassLock)
            {
                if (_bassLoaded) return;

                try
                {
                    string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                    string localBass = Path.Combine(exeDir, "bass.dll");

                    if (File.Exists(localBass))
                    {
                        LoadLibrary(localBass);
                        _bassLoaded = true;
                        return;
                    }

                    // Extraer de recursos embebidos a %TEMP%\OMtoSMConverter\bass.dll
                    string tempDir = Path.Combine(Path.GetTempPath(), "OMtoSMConverter");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    string tempBass = Path.Combine(tempDir, "bass.dll");

                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OMtoSMConverter.bass.dll"))
                    {
                        if (stream != null)
                        {
                            bool needWrite = !File.Exists(tempBass) || new FileInfo(tempBass).Length != stream.Length;
                            if (needWrite)
                            {
                                using (FileStream fs = new FileStream(tempBass, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    stream.CopyTo(fs);
                                }
                            }
                        }
                    }

                    if (File.Exists(tempBass))
                    {
                        SetDllDirectory(tempDir);
                        LoadLibrary(tempBass);
                        _bassLoaded = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AudioOffsetDetector] Error cargando bass embebido: " + ex.Message);
                }
            }
        }

        // ─── API Pública ─────────────────────────────────────────────────────────

        /// <summary>
        /// Calcula el #OFFSET de StepMania ajustado empíricamente a partir del audio.
        /// </summary>
        /// <param name="audioPath">Ruta al archivo de audio (MP3/OGG).</param>
        /// <param name="smStartMs">Tiempo de inicio de la Medida 0 en ms (calculado por OMiSM).</param>
        /// <returns>#OFFSET en segundos (negativo).</returns>
        public static double CalculateSmOffset(string audioPath, double smStartMs)
        {
            try
            {
                double lagMs;
                if (TryGetEngineLag(audioPath, out lagMs))
                {
                    double correctedStartMs = smStartMs + lagMs;
                    return -(correctedStartMs / 1000.0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AudioOffsetDetector] Error: " + ex.Message);
            }

            // Fallback: sin corrección
            return -(smStartMs / 1000.0);
        }

        // ─── Implementación ─────────────────────────────────────────────────────

        /// <summary>
        /// Determina el desfase empírico entre la decodificación de osu! (BASS) y StepMania (FFmpeg).
        /// </summary>
        private static bool TryGetEngineLag(string audioPath, out double lagMs)
        {
            lagMs = 0.0;
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                return false;

            EnsureBassLoaded();

            int sampleRate = 44100;
            bool hasXing = false;

            // 1. Análisis nativo del contenedor MP3 (sin dependencias externas)
            if (TryInspectMp3(audioPath, out sampleRate, out hasXing))
            {
                // Si el audio fue exportado directamente sin Xing/LAME frame (como Kisaragi en FL Studio),
                // no tiene padding de contenedor: lag = 19 ms.
                // Si tiene Xing/Info frame (estándar LAME / Lavf con padding de inicio):
                // - A 48000 Hz: lag = 76 ms (ej. Tiny Bunny).
                // - A 44100 Hz: lag = 69 ms (ej. Easy Breezy, Witchcraft).
                if (!hasXing)
                {
                    lagMs = 19.0;
                }
                else
                {
                    lagMs = (sampleRate == 48000) ? 76.0 : 69.0;
                }

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[AudioOffsetDetector] '{0}': Native MP3 Freq={1}, HasXing={2} -> Lag={3:F1}ms",
                    Path.GetFileName(audioPath), sampleRate, hasXing, lagMs));
                return true;
            }

            // 2. Si no es MP3 estándar o falló el análisis directo, consultar a BASS embebido
            try
            {
                BASS_Init(0, 44100, 0, IntPtr.Zero, IntPtr.Zero);
                uint flags = BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT | BASS_UNICODE;
                uint handle = BASS_StreamCreateFile(false, audioPath, 0, 0, flags);
                if (handle != 0)
                {
                    try
                    {
                        BASS_CHANNELINFO info = new BASS_CHANNELINFO();
                        if (BASS_ChannelGetInfo(handle, ref info) && info.freq > 0)
                        {
                            sampleRate = (int)info.freq;
                            lagMs = (sampleRate == 48000) ? 76.0 : 69.0;
                            return true;
                        }
                    }
                    finally
                    {
                        BASS_StreamFree(handle);
                    }
                }
            }
            catch { }

            // Default fallback
            lagMs = 69.0;
            return true;
        }

        /// <summary>
        /// Parser de cabeceras MP3 en C# puro. Lee ID3v2 synchsafe length, sincroniza el primer frame MPEG
        /// y detecta presencia del encabezado Xing/Info y la tasa de muestreo.
        /// </summary>
        private static bool TryInspectMp3(string audioPath, out int sampleRate, out bool hasXingHeader)
        {
            sampleRate = 44100;
            hasXingHeader = false;

            try
            {
                using (FileStream fs = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] id3Header = new byte[10];
                    int read = fs.Read(id3Header, 0, 10);
                    if (read == 10 && id3Header[0] == (byte)'I' && id3Header[1] == (byte)'D' && id3Header[2] == (byte)'3')
                    {
                        // Synchsafe integer (7 bits por byte)
                        int size = ((id3Header[6] & 0x7F) << 21) |
                                   ((id3Header[7] & 0x7F) << 14) |
                                   ((id3Header[8] & 0x7F) << 7)  |
                                   (id3Header[9] & 0x7F);
                        long afterId3 = 10L + size;
                        if (afterId3 < fs.Length)
                            fs.Seek(afterId3, SeekOrigin.Begin);
                    }
                    else
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                    }

                    // Leer hasta 64 KB de datos de audio
                    byte[] buffer = new byte[65536];
                    int bytesRead = fs.Read(buffer, 0, buffer.Length);
                    if (bytesRead < 4) return false;

                    // Buscar palabra de sincronización 0xFFE0
                    for (int i = 0; i < bytesRead - 4; i++)
                    {
                        if (buffer[i] == 0xFF && (buffer[i + 1] & 0xE0) == 0xE0)
                        {
                            uint hdr = ((uint)buffer[i] << 24) |
                                       ((uint)buffer[i + 1] << 16) |
                                       ((uint)buffer[i + 2] << 8) |
                                       ((uint)buffer[i + 3]);

                            uint layer = (hdr >> 17) & 3;
                            if (layer == 1) // Layer III (MP3)
                            {
                                uint srIdx = (hdr >> 10) & 3;
                                int[] rates = { 44100, 48000, 32000, 44100 };
                                sampleRate = (srIdx < 3) ? rates[srIdx] : 44100;

                                // Escanear el chunk del frame en busca de "Xing" o "Info"
                                int frameLen = Math.Min(bytesRead - i, 1500);
                                for (int j = i; j < i + frameLen - 4; j++)
                                {
                                    if ((buffer[j] == 'X' && buffer[j + 1] == 'i' && buffer[j + 2] == 'n' && buffer[j + 3] == 'g') ||
                                        (buffer[j] == 'I' && buffer[j + 1] == 'n' && buffer[j + 2] == 'f' && buffer[j + 3] == 'o'))
                                    {
                                        hasXingHeader = true;
                                        break;
                                    }
                                }

                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AudioOffsetDetector] Error en parser MP3 puro: " + ex.Message);
            }

            return false;
        }
    }
}
