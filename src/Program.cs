using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FatmaVision
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [STAThread]
        private static void Main(string[] args)
        {
            int rc = MfNative.MFStartup(MfConst.MF_VERSION, MfConst.MFSTARTUP_FULL);
            if (rc != MfConst.S_OK)
            {
                MessageBox.Show("MFStartup failed 0x" + rc.ToString("X8"),
                    "FatmaVision", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (args.Length > 0 && args[0] == "--selftest")
            {
                int code = SelfTest.Run(args);
                MfNative.MFShutdown();
                Environment.Exit(code);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string host = null;
            string port = null;
            bool debug = false;
            string setupHost = null;
            string stopHost = null;
            string setupUser = "root";
            int setupPort = 22;
            string setupPassword = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--host" && i + 1 < args.Length) { host = args[++i]; }
                else if (args[i] == "--port" && i + 1 < args.Length) { port = args[++i]; }
                else if (args[i] == "--debug") { debug = true; }
                else if (args[i] == "--setup-device" && i + 1 < args.Length) { setupHost = args[++i]; }
                else if (args[i] == "--stop-stream" && i + 1 < args.Length) { stopHost = args[++i]; }
                else if (args[i] == "--ssh-user" && i + 1 < args.Length) { setupUser = args[++i]; }
                else if (args[i] == "--ssh-port" && i + 1 < args.Length) { setupPort = int.Parse(args[++i]); }
                else if (args[i] == "--ssh-password" && i + 1 < args.Length) { setupPassword = args[++i]; }
                else if (args[i] != "--selftest")
                {
                    if (host == null) host = args[i];
                    else if (port == null) port = args[i];
                }
            }

            if (setupHost != null)
            {
                if (debug) SetupDebugConsole();
                DeviceSetup.Result r = DeviceSetup.Run(setupHost, setupUser, setupPort, setupPassword);
                foreach (string line in r.Log)
                {
                    Console.WriteLine(line);
                }
                MfNative.MFShutdown();
                Environment.Exit(r.Ok ? 0 : 1);
                return;
            }

            if (stopHost != null)
            {
                if (debug) SetupDebugConsole();
                try
                {
                    DeviceSetup.StopStream(stopHost, setupUser, setupPort, setupPassword, Console.WriteLine);
                    MfNative.MFShutdown();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("stop failed: " + ex.Message);
                    MfNative.MFShutdown();
                    Environment.Exit(1);
                }
                return;
            }

            if (debug) SetupDebugConsole();

            Application.Run(new MainForm(host, port));
            MfNative.MFShutdown();
            if (debug) Console.WriteLine("--- client exiting ---");
        }

        /// <summary>
        /// Attaches a Win32 console to the windowed exe and routes Form debug
        /// output to it. The same lines are mirrored to %TEMP%\ss2_debug.log so
        /// the session can be inspected remotely afterwards.
        /// </summary>
        private static void SetupDebugConsole()
        {
            AllocConsole();
            string path = Path.Combine(Path.GetTempPath(), "fv_debug.log");
            try { File.WriteAllText(path, "FatmaVision debug session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine); }
            catch { }
            Console.SetOut(new StreamWriter(new TeeStream(path)) { AutoFlush = true });
            Console.SetError(Console.Out);
            try { Console.Title = "FatmaVision debug"; } catch { }
            MainForm.DebugLog = s =>
            {
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
            };
            Console.WriteLine("debug console attached - log mirrors to " + path);
            Console.WriteLine("content hash line: MOVING = pixels changed this second.");
        }

        /// <summary>Mirrors console bytes into the session log file.</summary>
        private class TeeStream : Stream
        {
            private readonly StreamWriter _file;
            public TeeStream(string filePath)
            {
                _file = new StreamWriter(filePath, true) { AutoFlush = true };
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                try
                {
                    _file.Write(Encoding.UTF8.GetString(buffer, offset, count));
                }
                catch { }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _file.Close(); } catch { }
                }
                base.Dispose(disposing);
            }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return 0; } }
            public override long Position { get { return 0; } set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { return 0; }
            public override long Seek(long offset, SeekOrigin origin) { return 0; }
            public override void SetLength(long value) { }
        }
    }

    /// <summary>
    /// Headless verification: connects to a stream, decodes N frames,
    /// writes selftest.log, returns 0 on success. Used by the setup
    /// wizard's end-to-end check and the --selftest CLI flag.
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string[] args)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log");
            StringBuilder log = new StringBuilder();
            // live-append: each event hits the log file immediately so a hang
            // location stays visible after the fact
            Action<string> append = s =>
            {
                log.AppendLine(s);
                try { File.AppendAllText(logPath, s + Environment.NewLine, Encoding.UTF8); }
                catch { }
            };
            int want = 30;
            int timeoutSec = 30;
            string host = "127.0.0.1";
            int port = 5555;

            int idx = 1;
            bool doHash = false;
            while (idx < args.Length)
            {
                if (idx + 1 < args.Length && args[idx] == "--host") { host = args[idx + 1]; idx += 2; }
                else if (idx + 1 < args.Length && args[idx] == "--port") { port = int.Parse(args[idx + 1]); idx += 2; }
                else if (idx + 1 < args.Length && args[idx] == "--frames") { want = int.Parse(args[idx + 1]); idx += 2; }
                else if (idx + 1 < args.Length && args[idx] == "--timeout") { timeoutSec = int.Parse(args[idx + 1]); idx += 2; }
                else if (args[idx] == "--hash") { doHash = true; }
                else idx++;
            }

            int got = 0;
            Exception failure = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                MfDecoder dec = new MfDecoder();
                AnnexBClient client = new AnnexBClient(host, port);
                client.StatusChanged += s => append("[client] " + s);
                dec.StatusChanged += s => append("[decode] " + s);
                client.FrameReady += f => dec.Enqueue(f);
                dec.FrameReady += (w, h) =>
                {
                    int n = Interlocked.Increment(ref got);
                    if (doHash) append("frame " + n + " hash=" + FrameHash(dec));
                    if (n >= want) done.Set();
                };

                client.Start();
                dec.Start();
                bool ok = done.WaitOne(timeoutSec * 1000);
                client.Stop();
                dec.Stop();
                append("frames decoded: " + got + " / " + want);
                append("frames demuxed: " + client.FramesTotal);
                append("  vcl-boundary flushes: " + client.FlushVclCount);
                append("  seq (SPS/PPS) flushes: " + client.FlushSeqCount);
                append("bytes received: " + client.BytesTotal);
                append("result: " + (ok ? "PASS" : "FAIL (timeout or lost stream)"));
                if (failure != null) append("failure: " + failure);
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
                return ok ? 0 : 1;
            }
        }

        /// <summary>
        /// Decodes N frames from host:port (same pipeline as the GUI) and
        /// reports progress through the log callback. Used by the CLI selftest
        /// and the Device Setup Wizard verification step.
        /// </summary>
        public static bool RunCore(string host, int port, int want, int timeoutSec,
            Action<string> log, out int decoded, out int demuxed, out long bytesReceived)
        {
            int got = 0;
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                MfDecoder dec = new MfDecoder();
                AnnexBClient client = new AnnexBClient(host, port);
                client.StatusChanged += s => log("[client] " + s);
                dec.StatusChanged += s => log("[decode] " + s);
                client.FrameReady += f => dec.Enqueue(f);
                dec.FrameReady += (w, h) =>
                {
                    int n = Interlocked.Increment(ref got);
                    if (n >= want) done.Set();
                };

                client.Start();
                dec.Start();
                bool ok = done.WaitOne(timeoutSec * 1000);
                client.Stop();
                dec.Stop();
                decoded = got;
                demuxed = (int)client.FramesTotal;
                bytesReceived = client.BytesTotal;
                log("frames decoded: " + got + " / " + want + ", demuxed: " + client.FramesTotal +
                    ", bytes: " + client.BytesTotal + " -> " + (ok ? "PASS" : "FAIL"));
                return ok;
            }
        }

        /// <summary>FNV-1a over the published frame pixels (same as the GUI debug hash).</summary>
        private static string FrameHash(MfDecoder dec)
        {
            uint h = 2166136261u;
            bool ok = false;
            lock (dec.PublishLock)
            {
                System.Drawing.Bitmap f = dec.PublishedFrame;
                if (f != null)
                {
                    System.Drawing.Imaging.BitmapData bd = f.LockBits(
                        new System.Drawing.Rectangle(0, 0, f.Width, f.Height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] row = new byte[bd.Stride];
                        for (int y = 0; y < f.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, bd.Stride);
                            for (int i = 0; i < bd.Stride; i++) { h ^= row[i]; h *= 16777619; }
                        }
                        ok = true;
                    }
                    finally
                    {
                        f.UnlockBits(bd);
                    }
                }
            }
            return ok ? h.ToString("X8") : "(none)";
        }
    }
}
