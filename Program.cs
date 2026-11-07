using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ScreenRecorderLib;

namespace TinyScreenRec
{
    internal class Program
    {
        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        private static volatile Recorder? _recorder;
        private static volatile bool _isRecording;
        private static int _stopRequested; // 0 = belum, 1 = sudah (guard anti double-stop)
        private static readonly ManualResetEvent _recordFinishedEvent = new ManualResetEvent(false);

        private const long MinFreeDiskBytes = 500L * 1024 * 1024; // 500 MB
        private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(30);

        // ------------------------------------------------------------------
        // Preset (satu sumber kebenaran, tidak ada duplikasi default)
        // ------------------------------------------------------------------
        private static readonly List<(string Label, int Width, int Height, int Bitrate)> ResolutionPresets = new()
        {
            ("720p HD (1280x720) [Hemat Size - Default]",      1280,  720,  500 * 1000),
            ("1080p FHD (1920x1080) [Lebih Tajam, File Besar]", 1920, 1080, 1500 * 1000),
            ("480p (854x480) [Ultra Ringan]",                   854,  480,  300 * 1000),
            ("Native (Ukuran Asli Layar Tanpa Scaling)",          0,    0,  900 * 1000)
        };

        private static readonly List<(string Label, int Fps)> FpsPresets = new()
        {
            ("10 FPS [Super Hemat / Meeting & Log Dokumen]",              10),
            ("15 FPS [Seimbang untuk Coding/Presentasi - Default]",       15),
            ("24 FPS [Standar Video Halus]",                              24),
            ("30 FPS [Paling Halus / Ukuran File Lebih Besar]",           30)
        };

        private static int _resIndex = 0;
        private static int _fpsIndex = 1;

        // ------------------------------------------------------------------
        // Logging (konsol + file)
        // ------------------------------------------------------------------
        private static readonly object _logLock = new object();
        private static string? _logFilePath;

        private static string GetOutputDirectory()
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
                videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(videos, "TinyScreenRec");
        }

        private static void InitFileLog()
        {
            try
            {
                string dir = GetOutputDirectory();
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, "app.log");
            }
            catch
            {
                _logFilePath = null; // logging file dimatikan, konsol tetap jalan
            }
        }

        private static void WriteLog(string level, string message, ConsoleColor color)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{level,-5}] {message}";
            lock (_logLock)
            {
                try
                {
                    Console.ForegroundColor = color;
                    Console.WriteLine(line);
                }
                catch { /* konsol bermasalah, abaikan */ }
                finally
                {
                    try { Console.ResetColor(); } catch { }
                }

                if (_logFilePath != null)
                {
                    try { File.AppendAllText(_logFilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); }
                    catch { /* disk penuh / file terkunci, abaikan */ }
                }
            }
        }

        private static void LogInfo(string m) => WriteLog("INFO", m, ConsoleColor.Cyan);
        private static void LogSuccess(string m) => WriteLog("OK", m, ConsoleColor.Green);
        private static void LogWarn(string m) => WriteLog("WARN", m, ConsoleColor.Yellow);
        private static void LogError(string m) => WriteLog("ERROR", m, ConsoleColor.Red);

        // ------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------
        static void Main(string[] args)
        {
            Console.Title = "Tiny CLI Screen";
            InitFileLog();

            // Tangkap crash yang lolos (termasuk dari thread native) supaya ada jejaknya
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogError($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}): {e.ExceptionObject}");
                try { StopSession(); } catch { }
            };

            // Ctrl+C: kalau sedang rekam, stop dengan aman; kalau tidak, biarkan keluar normal
            Console.CancelKeyPress += (s, e) =>
            {
                if (_isRecording)
                {
                    e.Cancel = true;
                    LogWarn("Ctrl+C diterima. Menghentikan rekaman dengan aman...");
                    StopSession();
                }
            };

            while (true)
            {
                ShowMainMenu();
                Console.Write("\nPilih menu [1-4]: ");

                string? raw = Console.ReadLine();
                if (raw == null) return; // stdin tertutup -> keluar, hindari busy-loop
                string input = raw.Trim();

                switch (input)
                {
                    case "1":
                        StartRecordingSession();
                        break;
                    case "2":
                        SelectResolutionMenu();
                        break;
                    case "3":
                        SelectFpsMenu();
                        break;
                    case "4":
                        LogInfo("Aplikasi keluar. Sampai jumpa!");
                        return;
                    default:
                        LogWarn("Pilihan tidak valid. Silakan pilih 1, 2, 3, atau 4.");
                        Thread.Sleep(1000);
                        break;
                }
            }
        }

        private static void SafeClear()
        {
            try
            {
                if (!Console.IsOutputRedirected)
                    Console.Clear();
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Menu
        // ------------------------------------------------------------------
        private static void ShowMainMenu()
        {
            SafeClear();
            Console.WriteLine("==================================================");
            Console.WriteLine("               TINY CLI SCREEN v1.0               ");
            Console.WriteLine("==================================================");
            Console.WriteLine(" [Konfigurasi Aktif]");
            Console.WriteLine($" > Resolusi   : {ResolutionPresets[_resIndex].Label}");
            Console.WriteLine($" > Framerate  : {FpsPresets[_fpsIndex].Fps} FPS");
            Console.WriteLine($" > Folder     : {GetOutputDirectory()}");
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(" [1] Mulai (Start)");
            Console.WriteLine(" [2] Ubah Resolusi");
            Console.WriteLine(" [3] Ubah Frame Rate (FPS)");
            Console.WriteLine(" [4] Keluar (Exit)");
            Console.WriteLine("==================================================");
        }

        private static void SelectResolutionMenu()
        {
            while (true)
            {
                SafeClear();
                Console.WriteLine("==================================================");
                Console.WriteLine("                  PILIH RESOLUSI                  ");
                Console.WriteLine("==================================================");
                for (int i = 0; i < ResolutionPresets.Count; i++)
                {
                    string mark = i == _resIndex ? " [*]" : "";
                    Console.WriteLine($" [{i + 1}] {ResolutionPresets[i].Label}{mark}");
                }
                Console.WriteLine(" [0] Kembali ke Menu Utama");
                Console.WriteLine("--------------------------------------------------");
                Console.Write("Pilihan Anda: ");

                string? raw = Console.ReadLine();
                if (raw == null) return;
                string choice = raw.Trim();
                if (choice == "0") return;

                if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= ResolutionPresets.Count)
                {
                    _resIndex = idx - 1;
                    LogSuccess($"Resolusi diubah ke: {ResolutionPresets[_resIndex].Label}");
                    Thread.Sleep(1000);
                    return;
                }

                LogWarn("Pilihan preset resolusi tidak valid.");
                Thread.Sleep(1000);
            }
        }

        private static void SelectFpsMenu()
        {
            while (true)
            {
                SafeClear();
                Console.WriteLine("==================================================");
                Console.WriteLine("             PILIH FRAME RATE (FPS)               ");
                Console.WriteLine("==================================================");
                for (int i = 0; i < FpsPresets.Count; i++)
                {
                    string mark = i == _fpsIndex ? " [*]" : "";
                    Console.WriteLine($" [{i + 1}] {FpsPresets[i].Label}{mark}");
                }
                Console.WriteLine(" [0] Kembali ke Menu Utama");
                Console.WriteLine("--------------------------------------------------");
                Console.Write("Pilihan Anda: ");

                string? raw = Console.ReadLine();
                if (raw == null) return;
                string choice = raw.Trim();
                if (choice == "0") return;

                if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= FpsPresets.Count)
                {
                    _fpsIndex = idx - 1;
                    LogSuccess($"Framerate diubah ke: {FpsPresets[_fpsIndex].Fps} FPS");
                    Thread.Sleep(1000);
                    return;
                }

                LogWarn("Pilihan preset FPS tidak valid.");
                Thread.Sleep(1000);
            }
        }

        // ------------------------------------------------------------------
        // Recording
        // ------------------------------------------------------------------
        private static bool TryPrepareOutputPath(out string outputPath)
        {
            outputPath = string.Empty;
            try
            {
                string dir = GetOutputDirectory();
                Directory.CreateDirectory(dir);

                string? root = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.AvailableFreeSpace < MinFreeDiskBytes)
                    {
                        LogError($"Sisa disk kurang dari {MinFreeDiskBytes / 1024 / 1024} MB. Rekaman dibatalkan.");
                        return false;
                    }
                }

                outputPath = Path.Combine(dir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                // Uji tulis: pastikan folder benar-benar bisa ditulis
                string probe = Path.Combine(dir, ".write_test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Folder output tidak bisa dipakai: {ex.Message}");
                return false;
            }
        }

        private static void DetectAudioDevices(out bool hasMic, out bool hasOut)
        {
            hasMic = false;
            hasOut = false;
            try
            {
                hasMic = Recorder.GetSystemAudioCaptureDevices().Count > 0;
                hasOut = Recorder.GetSystemAudioLoopbackDevices().Count > 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Gagal membaca daftar audio device: {ex.Message}");
            }

            if (!hasMic) LogWarn("Mic tidak terdeteksi, rekam tanpa mic.");
            if (!hasOut) LogWarn("Output device tidak terdeteksi, rekam tanpa audio internal.");
        }

        private static void StartRecordingSession()
        {
            if (!TryPrepareOutputPath(out string outputPath))
            {
                WaitKey();
                return;
            }

            DetectAudioDevices(out bool hasMic, out bool hasOut);

            var res = ResolutionPresets[_resIndex];
            int fps = FpsPresets[_fpsIndex].Fps;

            var audioSources = new List<AudioSourceBase>();
            if (hasOut)
            {
                var defOut = LoopbackAudioSource.Default;
                if (defOut != null) audioSources.Add(defOut);
                else audioSources.Add(new LoopbackAudioSource());
            }
            if (hasMic)
            {
                var defMic = CaptureAudioSource.Default;
                if (defMic != null) audioSources.Add(defMic);
                else audioSources.Add(new CaptureAudioSource());
            }

            var outputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                Stretch = StretchMode.Uniform
            };
            if (res.Width > 0 && res.Height > 0)
                outputOptions.OutputFrameSize = new ScreenSize(res.Width, res.Height);

            var recorderOptions = new RecorderOptions
            {
                OutputOptions = outputOptions,
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Framerate = fps,
                    Bitrate = res.Bitrate,
                    IsFixedFramerate = false,
                    IsFragmentedMp4Enabled = true,
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode = H264BitrateControlMode.UnconstrainedVBR
                    }
                },
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = hasMic || hasOut,
                    Bitrate = AudioBitrate.bitrate_96kbps,
                    Channels = AudioChannels.Stereo,
                    AudioSources = audioSources
                }
            };

            _recordFinishedEvent.Reset();
            Interlocked.Exchange(ref _stopRequested, 0);

            Recorder? recorder = null;
            try
            {
                recorder = Recorder.CreateRecorder(recorderOptions);
                _recorder = recorder;

                // Handler dipanggil dari thread native: wajib try/catch
                recorder.OnRecordingComplete += (s, e) =>
                {
                    try { LogSuccess($"Rekaman tersimpan: {e.FilePath}"); }
                    catch { }
                    finally
                    {
                        _isRecording = false;
                        _recordFinishedEvent.Set();
                    }
                };

                recorder.OnRecordingFailed += (s, e) =>
                {
                    try { LogError($"Rekaman gagal: {e.Error}"); }
                    catch { }
                    finally
                    {
                        _isRecording = false;
                        _recordFinishedEvent.Set();
                    }
                };

                recorder.OnStatusChanged += (s, e) =>
                {
                    try
                    {
                        switch (e.Status)
                        {
                            case RecorderStatus.Recording:
                                LogSuccess("STATUS: Sedang berlangsung...");
                                break;
                            case RecorderStatus.Paused:
                                LogWarn("STATUS: Pause...");
                                break;
                            case RecorderStatus.Finishing:
                                LogInfo("STATUS: Finalisasi file...");
                                break;
                        }
                    }
                    catch { }
                };

                PrintRecordingHeader(outputPath, res, fps, hasMic, hasOut);

                _isRecording = true;            // di-set SEBELUM Record() agar tidak ditimpa event cepat
                recorder.Record(outputPath);

                // Loop kontrol keyboard
                while (_isRecording)
                {
                    if (!Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        ConsoleKey key = Console.ReadKey(intercept: true).Key;

                        if (key == ConsoleKey.P)
                        {
                            var status = recorder.Status;
                            if (status == RecorderStatus.Recording)
                                recorder.Pause();
                            else if (status == RecorderStatus.Paused)
                                recorder.Resume();
                        }
                        else if (key == ConsoleKey.S)
                        {
                            LogInfo("Mengirim perintah STOP...");
                            StopSession();
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }

                if (!_recordFinishedEvent.WaitOne(FinalizeTimeout))
                    LogError("Timeout menunggu finalisasi. File mungkin tidak lengkap.");
            }
            catch (Exception ex)
            {
                _isRecording = false;
                LogError($"Gagal memulai/menjalankan rekaman: {ex.Message}");
            }
            finally
            {
                _recorder = null;
                try { recorder?.Dispose(); }   // lepas resource native (D3D, WASAPI, MF)
                catch (Exception ex) { LogWarn($"Dispose recorder bermasalah: {ex.Message}"); }
            }

            WaitKey();
        }

        private static void PrintRecordingHeader(string outputPath, (string Label, int Width, int Height, int Bitrate) res,
                                                 int fps, bool hasMic, bool hasOut)
        {
            SafeClear();
            Console.WriteLine("==================================================");
            Console.WriteLine("                   SESI DIMULAI                   ");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Output File : {outputPath}");
            Console.WriteLine($"Resolusi    : {(res.Width > 0 ? $"{res.Width}x{res.Height}" : "Native")}");
            Console.WriteLine($"Framerate   : {fps} FPS");
            Console.WriteLine($"Audio       : Internal={(hasOut ? "ON" : "OFF")}, Mic={(hasMic ? "ON" : "OFF")}");
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(" Kontrol Shortcut:");
            Console.WriteLine("  [P] : Pause / Resume");
            Console.WriteLine("  [S] : Stop & Simpan File");
            Console.WriteLine("  Ctrl+C juga aman (rekaman difinalisasi dulu)");
            Console.WriteLine("==================================================");
        }

        private static void StopSession()
        {
            var r = _recorder;
            if (r == null) return;

            // Cegah Stop() ganda (tombol S + Ctrl+C hampir bersamaan)
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1) return;

            try
            {
                var status = r.Status;
                if (status == RecorderStatus.Recording || status == RecorderStatus.Paused)
                {
                    LogInfo("Menghentikan recorder...");
                    r.Stop();
                }
            }
            catch (Exception ex)
            {
                LogError($"Gagal stop: {ex.Message}");
            }
        }

        private static void WaitKey()
        {
            if (Console.IsInputRedirected) return;

            // Kosongkan buffer agar tombol yang tertekan saat finalisasi tidak langsung menutup layar
            try { while (Console.KeyAvailable) Console.ReadKey(true); } catch { }

            Console.WriteLine("\nTekan sembarang tombol untuk kembali ke Menu Utama...");
            try { Console.ReadKey(true); } catch { }
        }
    }
}