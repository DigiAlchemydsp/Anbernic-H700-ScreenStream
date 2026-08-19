using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FatmaVision
{
    public class MainForm : Form
    {
        /// <summary>Debug console sink (set by Program when --debug). Null in release.</summary>
        public static Action<string> DebugLog;

        private static void Log(string msg)
        {
            Action<string> h = DebugLog;
            if (h != null) h(msg);
        }

        private class VideoPanel : Panel
        {
            private MfDecoder _dec;
            private int _vw = 640, _vh = 480;
            private long _lastFrame;

            public VideoPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Black;
            }

            public void Attach(MfDecoder dec)
            {
                _dec = dec;
                Invalidate();
            }

            public void NotifyFrame(int w, int h)
            {
                _vw = w;
                _vh = h;
                _lastFrame = Environment.TickCount;
                Invalidate();
            }

            public void Clear()
            {
                _dec = null;
                _vw = 640;
                _vh = 480;
                _lastFrame = 0;
                Invalidate();
            }

            /// <summary>True while frames keep arriving (3 s grace).</summary>
            private bool HasSignal
            {
                get { return _lastFrame != 0 && Environment.TickCount - _lastFrame < 3000; }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Black);
                int panelW = ClientSize.Width;
                int panelH = ClientSize.Height;
                if (panelW < 8 || panelH < 8) return;
                MfDecoder dec = _dec;
                if (dec == null)
                {
                    DrawIdle(e.Graphics, panelW, panelH);
                    return;
                }

                if (!HasSignal)
                {
                    using (Font f = new Font("Segoe UI", 16, FontStyle.Bold))
                    {
                        string msg = "NO SIGNAL - waiting for stream...";
                        SizeF sz = e.Graphics.MeasureString(msg, f);
                        e.Graphics.DrawString(msg, f, Brushes.Goldenrod,
                            (panelW - sz.Width) / 2, (panelH - sz.Height) / 2);
                    }
                    return;
                }

                int scale = Math.Max(1, Math.Min(panelW / _vw, panelH / _vh));
                int dw = _vw * scale;
                int dh = _vh * scale;
                int dx = (panelW - dw) / 2;
                int dy = (panelH - dh) / 2;

                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                lock (dec.PublishLock)
                {
                    Bitmap f = dec.PublishedFrame;
                    if (f != null)
                        e.Graphics.DrawImage(f, new Rectangle(dx, dy, dw, dh),
                            0, 0, _vw, _vh, GraphicsUnit.Pixel);
                }
            }

            /// <summary>Idle screen: waiting text + first-time hints.</summary>
            private void DrawIdle(Graphics g, int w, int h)
            {
                using (Font f1 = new Font("Segoe UI", 16, FontStyle.Bold))
                {
                    string msg = "FatmaVision waiting for client";
                    SizeF sz = g.MeasureString(msg, f1);
                    g.DrawString(msg, f1, Brushes.White,
                        (w - sz.Width) / 2, (h - sz.Height) / 2 - 24);
                }
                using (Font f2 = new Font("Segoe UI", 10))
                {
                    string hint = "First time: Set Up Device - enter your handheld's IP and it does the rest.";
                    SizeF sz = g.MeasureString(hint, f2);
                    g.DrawString(hint, f2, Brushes.Gray,
                        (w - sz.Width) / 2, (h - sz.Height) / 2 + 12);
                    string hint2 = "After that, Connect starts the stream on the device automatically.";
                    SizeF sz2 = g.MeasureString(hint2, f2);
                    g.DrawString(hint2, f2, Brushes.Gray,
                        (w - sz2.Width) / 2, (h - sz2.Height) / 2 + 40);
                }
            }
        }

        private readonly Button _connect;
        private readonly Button _setup;
        private readonly Button _recordBtn;
        private readonly VideoPanel _video;
        private readonly Panel _bar;
        private readonly StatusStrip _status;
        private readonly ToolStripStatusLabel _stateLabel;
        private readonly ToolStripStatusLabel _metricsLabel;
        private readonly System.Windows.Forms.Timer _ticker;

        private AnnexBClient _client;
        private MfDecoder _decoder;
        private MkvRecorder _recorder;

        private volatile string _state = "Idle";
        private volatile string _metrics = "";
        private long _lastBytes;
        private long _lastFrames;
        private long _lastTick;
        private bool _connected;
        private string _sshUser = "root";
        private int _sshPort = 22;
        private string _deviceHost = "";
        private string _devicePort = "5555";
        private string _deviceOs = "auto";

        // debug content sampling (1 Hz, only when DebugLog != null)
        private long _hashTick;
        private uint _hashSample;
        private bool _hashValid;

        private readonly string _settingsPath;

        public MainForm(string host, string port)
        {
            Text = "FatmaVision";
            ClientSize = new Size(1000, 780);
            MinimumSize = new Size(720, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.White;

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FatmaVision", "settings.txt");

            var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(45, 45, 45) };
            _bar = bar;

            _connect = new Button
            {
                Text = "Connect",
                Location = new Point(12, 7),
                Size = new Size(86, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatAppearance = { BorderColor = Color.FromArgb(90, 90, 90) }
            };
            _connect.Click += OnConnectClick;
            bar.Controls.Add(_connect);

            _setup = new Button
            {
                Text = "Set Up Device",
                Location = new Point(104, 7),
                Size = new Size(118, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 60, 80),
                ForeColor = Color.White,
                FlatAppearance = { BorderColor = Color.FromArgb(90, 90, 90) }
            };
            _setup.Click += OnSetupClick;
            bar.Controls.Add(_setup);

            _recordBtn = new Button
            {
                Text = "Start Recording",
                Location = new Point(228, 7),
                Size = new Size(120, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatAppearance = { BorderColor = Color.FromArgb(90, 90, 90) }
            };
            _recordBtn.Click += OnRecordClick;
            bar.Controls.Add(_recordBtn);

            _video = new VideoPanel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };

            _status = new StatusStrip { SizingGrip = false, BackColor = Color.FromArgb(45, 45, 45) };
            _stateLabel = new ToolStripStatusLabel { Text = "Idle", ForeColor = Color.Gray };
            _status.Items.Add(_stateLabel);
            _metricsLabel = new ToolStripStatusLabel { Text = "", ForeColor = Color.DimGray };
            _status.Items.Add(_metricsLabel);

            Controls.Add(_video);
            Controls.Add(bar);
            Controls.Add(_status);

            LoadSettings();
            if (host != null) _deviceHost = host;
            if (port != null) _devicePort = port;

            _ticker = new System.Windows.Forms.Timer { Interval = 500 };
            _ticker.Tick += OnTick;
            _ticker.Start();
            FormClosed += OnClosed;
            KeyPreview = true;
            KeyDown += OnKeyDown;

            if (host != null)
            {
                Shown += (s, e) => OnConnectClick(s, e);
            }
        }

        // ---------- fullscreen toggle (F key) ----------

        private bool _fullscreen;
        private System.Drawing.Rectangle _prevBounds;
        private FormBorderStyle _prevBorder;

        /// <summary>
        /// Pressing F switches the window between the normal chrome (button
        /// bar + status strip) and a borderless fullscreen view of the video.
        /// </summary>
        private void ToggleFullscreen()
        {
            _fullscreen = !_fullscreen;
            if (_fullscreen)
            {
                _prevBounds = Bounds;
                _prevBorder = FormBorderStyle;
                _bar.Visible = false;
                _status.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;
                TopMost = true;
                Focus();
                Log("view: fullscreen ON (press F to exit)");
            }
            else
            {
                TopMost = false;
                FormBorderStyle = _prevBorder;
                _bar.Visible = true;
                _status.Visible = true;
                Bounds = _prevBounds;
                Log("view: fullscreen OFF");
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        // ---------- settings persistence ----------

        private void LoadSettings()
        {
            try
            {
                // migrate from the old ScreenStream2 settings location
                string oldPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ScreenStream2", "settings.txt");
                if (!File.Exists(_settingsPath) && File.Exists(oldPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                    File.Copy(oldPath, _settingsPath);
                }
                if (File.Exists(_settingsPath))
                {
                    string[] lines = File.ReadAllLines(_settingsPath, Encoding.UTF8);
                    if (lines.Length > 0 && lines[0].Trim().Length > 0) _deviceHost = lines[0].Trim();
                    if (lines.Length > 1 && lines[1].Trim().Length > 0) _devicePort = lines[1].Trim();
                    if (lines.Length > 2 && lines[2].Trim().Length > 0) _sshUser = lines[2].Trim();
                    if (lines.Length > 3)
                    {
                        int p;
                        if (int.TryParse(lines[3].Trim(), out p) && p > 0) _sshPort = p;
                    }
                    if (lines.Length > 4 && lines[4].Trim().Length > 0) _deviceOs = lines[4].Trim();
                }
            }
            catch { }
            if (_devicePort.Length == 0) _devicePort = "5555";
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                File.WriteAllLines(_settingsPath,
                    new[] { _deviceHost, _devicePort, _sshUser, _sshPort.ToString(), _deviceOs }, Encoding.UTF8);
            }
            catch { }
        }

        // ---------- device setup wizard ----------

        private void OnSetupClick(object sender, EventArgs e)
        {
            using (DeviceWizard wiz = new DeviceWizard(_deviceHost, _sshUser, _sshPort, _deviceOs))
            {
                if (wiz.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(wiz.Host))
                {
                    _deviceHost = wiz.Host;
                    _devicePort = "5555";
                    _sshUser = wiz.SshUser;
                    _sshPort = wiz.SshPort;
                    _deviceOs = wiz.OsKey;
                    SaveSettings();
                    if (wiz.ConnectAfter && !_connected)
                    {
                        try { Connect(); }
                        catch (Exception ex) { _state = "Error: " + ex.Message; }
                    }
                }
            }
        }

        // ---------- connect / disconnect ----------

        private bool _ensuring;

        /// <summary>
        /// Connect = "set and forget": before opening the video socket, ensure
        /// the device is actually streaming by SSH (deploys stream.sh if
        /// missing, starts it if down). No need to touch the device by hand.
        /// If SSH is unavailable the connect is attempted anyway (the stream
        /// may already be running).
        /// </summary>
        private void OnConnectClick(object sender, EventArgs e)
        {
            if (_connected) { Disconnect(); return; }
            if (_ensuring) return;
            _ensuring = true;
            string host = _deviceHost;
            string user = _sshUser;
            int sshPort = _sshPort;
            _state = "checking device stream ...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string fail = null;
                try
                {
                    DeviceSetup.EnsureStream(host, user, sshPort, null,
                        s => { Log("setup: " + s); _state = s; });
                }
                catch (Exception ex)
                {
                    fail = "device auto-start failed (" + ex.Message + ") - trying to connect anyway";
                }
                if (IsDisposed) { _ensuring = false; return; }
                if (!IsHandleCreated)
                {
                    _ensuring = false;
                    return;
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _ensuring = false;
                        if (fail != null) Log(fail);
                        try { Connect(); }
                        catch (Exception ex2)
                        {
                            _state = "Error: " + ex2.Message;
                            Log("connect error: " + ex2.Message);
                        }
                        if (fail != null) _state = fail;
                    }));
                }
                catch
                {
                    _ensuring = false;
                }
            });
        }

        private void Connect()
        {
            string host = _deviceHost;
            string port = _devicePort;
            int portNum;
            if (host.Length == 0 || !int.TryParse(port, out portNum) || portNum <= 0)
                throw new Exception("no device IP - run Set Up Device first");
            SaveSettings();
            Log("connect to " + host + ":" + portNum);

            _decoder = new MfDecoder();
            _decoder.StatusChanged += s => { _state = s; Log("decode: " + s); };
            _decoder.FrameReady += (w, h) => _video.NotifyFrame(w, h);
            _decoder.Start();
            _video.Attach(_decoder);

            _client = new AnnexBClient(host, portNum);
            _client.StatusChanged += s => { _state = s; Log("net: " + s); };
            _client.Reconnected += () => { Log("net: stream reset - decoder resync"); if (_decoder != null) _decoder.Reset(); };
            _client.FrameReady += f =>
            {
                if (_decoder != null) _decoder.Enqueue(f);
                if (_recorder != null) _recorder.AddFrame(f);
            };
            _client.Start();

            _connected = true;
            _connect.Text = "Disconnect";
            _lastBytes = _client.BytesTotal;
            _lastFrames = _decoder.FramesDecoded;
            _lastTick = Environment.TickCount;
            _state = "connecting ...";
        }

        private void Disconnect()
        {
            Log("disconnect by user");
            StopRecording();
            if (_client != null) { _client.Stop(); _client = null; }
            if (_decoder != null) { _decoder.Stop(); _decoder = null; }
            _connected = false;
            _connect.Text = "Connect";
            _state = "Disconnected - stopping device stream ...";
            _video.Clear();

            // stop the stream script on the device too (fire-and-forget)
            string host = _deviceHost;
            string user = _sshUser;
            int sshPort = _sshPort;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DeviceSetup.StopStream(host, user, sshPort, null,
                        s => Log("setup: " + s));
                }
                catch (Exception ex)
                {
                    Log("device stop failed: " + ex.Message);
                }
            });
        }

        // ---------- recording (.mkv next to the exe, in Captures\) ----------

        /// <summary>
        /// Toggle button: Start Recording -&gt; Stop Recording. Starting is only
        /// meaningful while connected; stopping finalizes the MKV properly
        /// (patches cluster/segment sizes).
        /// </summary>
        private void OnRecordClick(object sender, EventArgs e)
        {
            if (_recorder != null)
            {
                StopRecording();
                return;
            }
            if (!_connected)
            {
                _state = "connect before recording";
                return;
            }
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Captures");
                Directory.CreateDirectory(dir);
                string name = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mkv";
                _recorder = new MkvRecorder(Path.Combine(dir, name));
                _recordBtn.Text = "Stop Recording";
                _recordBtn.BackColor = Color.FromArgb(140, 45, 45);
                _state = "recording to Captures\\" + name;
                Log("recording started: " + name);
            }
            catch (Exception ex)
            {
                _state = "record error: " + ex.Message;
            }
        }

        private void StopRecording()
        {
            if (_recorder == null) return;
            try { _recorder.Close(); } catch { }
            _recorder = null;
            _recordBtn.Text = "Start Recording";
            _recordBtn.BackColor = Color.FromArgb(70, 70, 70);
            _state = "recording stopped";
            Log("recording stopped");
        }

        // ---------- status ticker ----------

        /// <summary>
        /// FNV-1a hash of the currently displayed frame, sampled at 1 Hz.
        /// Compares against the previous sample: "MOVING" means the pixels on
        /// screen changed, "STILL" means the decoder delivers an identical
        /// picture (likely a frozen fb0), "(no frame)" means nothing decodes.
        /// </summary>
        private string SampleContentHash()
        {
            MfDecoder dec = _decoder;
            if (dec == null) return "hash=(no frame)";
            uint h = 2166136261u;
            bool ok = false;
            lock (dec.PublishLock)
            {
                Bitmap f = dec.PublishedFrame;
                if (f != null)
                {
                    BitmapData bd = f.LockBits(new Rectangle(0, 0, f.Width, f.Height),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] row = new byte[bd.Stride];
                        for (int y = 0; y < f.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, bd.Stride);
                            for (int i = 0; i < bd.Stride; i++)
                            {
                                h ^= row[i];
                                h *= 16777619;
                            }
                        }
                        ok = true;
                    }
                    finally
                    {
                        f.UnlockBits(bd);
                    }
                }
            }
            if (!ok) return "hash=(no frame yet)";
            string verdict = !_hashValid ? "first sample"
                : (h == _hashSample ? "STILL" : "MOVING");
            _hashSample = h;
            _hashValid = true;
            return "hash=" + h.ToString("X8") + " " + verdict;
        }

        private void OnTick(object sender, EventArgs e)
        {
            long now = Environment.TickCount;
            if (!_connected)
            {
                _metrics = "";
            }
            else
            {
                long dt = now - _lastTick;
                if (dt >= 450)
                {
                    long bytes = _client.BytesTotal;
                    long frames = _decoder.FramesDecoded;
                    long dBytes = bytes - _lastBytes;
                    long dFrames = frames - _lastFrames;
                    _lastBytes = bytes;
                    _lastFrames = frames;
                    _lastTick = now;
                    double kbps = dBytes * 8.0 / 1000.0 / (dt / 1000.0);
                    double fps = dFrames / (dt / 1000.0);
                    _metrics = string.Format("  {0:F1} fps | {1:F0} kbit/s | {2} frames | {3} KB",
                        fps, kbps, frames, bytes / 1024);
                    if (DebugLog != null && now - _hashTick >= 1000)
                    {
                        _hashTick = now;
                        Log(string.Format("conn LIVE frames={0} ({1:F1} fps) bytes={2} ({3:F0} kB/s) {4}",
                            frames, fps, bytes, kbps / 8.0, SampleContentHash()));
                    }
                }
            }
            _stateLabel.Text = _state;
            _stateLabel.ForeColor = _state.IndexOf("Error") >= 0 ? Color.OrangeRed
                : (_state == "Disconnected" || _state == "Idle" ? Color.Gray : Color.LimeGreen);
            _metricsLabel.Text = _metrics;
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            _ticker.Stop();
            Disconnect();
        }
    }
}
