using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FatmaVision
{
    /// <summary>
    /// Device Setup Wizard: probes the handheld over SSH, deploys or repairs
    /// stream.sh + ports-screenstream.sh, (re)starts the stream on :5555 and
    /// verifies it end-to-end. One button does the whole job ("set it and
    /// forget it"). The device IP is saved in the client settings afterwards,
    /// so the viewer auto-connects on the next launch.
    /// </summary>
    public class DeviceWizard : Form
    {
        private readonly TextBox _ip;
        private readonly TextBox _user;
        private readonly TextBox _port;
        private readonly TextBox _password;
        private readonly ComboBox _os;
        private readonly TextBox _log;
        private readonly Button _go;
        private readonly Button _test;
        private readonly Button _done;
        private readonly CheckBox _connectAfter;
        private readonly Label _status;

        public string Host { get; private set; }
        public string SshUser { get { return _user.Text.Trim(); } }
        public int SshPort
        {
            get { int p; return int.TryParse(_port.Text.Trim(), out p) ? p : 22; }
        }
        public bool ConnectAfter { get { return _connectAfter.Checked; } }
        public string OsKey
        {
            get
            {
                switch (_os.SelectedIndex)
                {
                    case 1: return "knulli";
                    case 2: return "batocera";
                    case 3: return "arkos";
                    case 4: return "amberelec";
                    case 5: return "rocknix";
                    case 6: return "muos";
                    case 7: return "generic";
                    default: return "auto";
                }
            }
        }

        private static readonly string[] OsNames = new string[]
        {
            "Auto-detect (recommended)", "Knulli", "Batocera", "ArkOS",
            "AmberELEC", "ROCKNIX", "muOS", "Generic Linux"
        };

        /// <summary>Known SSH defaults per OS (prefills the fields; detection itself is automatic).</summary>
        private static readonly Dictionary<string, string[]> SshDefaults = new Dictionary<string, string[]>
        {
            { "knulli",    new[] { "root", "" } },
            { "batocera",  new[] { "root", "" } },
            { "amberelec", new[] { "root", "" } },
            { "rocknix",   new[] { "root", "" } },
            { "arkos",     new[] { "ark", "ark" } },
            { "muos",      new[] { "root", "muos" } },
            { "generic",   new[] { "root", "" } }
        };

        public DeviceWizard(string prefillHost, string prefillUser, int prefillPort, string prefillOs)
        {
            Text = "FatmaVision - Device Setup Wizard";
            ClientSize = new Size(640, 600);
            MinimumSize = new Size(620, 580);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            int y = 16;
            AddLabel("Device IP or hostname", 16, y + 3, 180);
            _ip = AddBox(200, y, 200);
            y += 36;
            AddLabel("Device OS", 16, y + 3, 180);
            _os = new ComboBox
            {
                Location = new Point(200, y),
                Size = new Size(220, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            foreach (string n in OsNames) _os.Items.Add(n);
            _os.SelectedIndex = 0;
            _os.SelectedIndexChanged += OnOsChanged;
            Controls.Add(_os);
            var osHint = new Label
            {
                Text = "Prefills the SSH defaults below. Detection on the device is automatic either way.",
                Location = new Point(430, y + 3),
                AutoSize = false,
                Size = new Size(196, 28),
                ForeColor = Color.FromArgb(150, 150, 150)
            };
            Controls.Add(osHint);
            y += 36;
            AddLabel("SSH user", 16, y + 3, 180);
            _user = AddBox(200, y, 200);
            y += 36;
            AddLabel("SSH port", 16, y + 3, 180);
            _port = AddBox(200, y, 200);
            y += 36;
            AddLabel("SSH password (optional)", 16, y + 3, 180);
            _password = AddBox(200, y, 200);
            _password.UseSystemPasswordChar = true;
            y += 30;
            var hint = new Label
            {
                Text = "Leave empty for key auth. A password only works when PuTTY plink.exe is on PATH.",
                Location = new Point(200, y),
                AutoSize = false,
                Size = new Size(420, 30),
                ForeColor = Color.FromArgb(150, 150, 150)
            };
            Controls.Add(hint);
            y += 40;

            _go = new Button
            {
                Text = "Set Up Device & Start Stream",
                Location = new Point(16, y),
                Size = new Size(230, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                FlatAppearance = { BorderColor = Color.FromArgb(90, 90, 90) }
            };
            _go.Click += OnGoClick;
            Controls.Add(_go);

            _test = new Button
            {
                Text = "Test SSH Connection",
                Location = new Point(262, y),
                Size = new Size(160, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatAppearance = { BorderColor = Color.FromArgb(90, 90, 90) }
            };
            _test.Click += OnTestClick;
            Controls.Add(_test);

            _connectAfter = new CheckBox
            {
                Text = "Connect viewer after setup",
                Location = new Point(438, y + 6),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };
            Controls.Add(_connectAfter);
            y += 44;

            _status = new Label
            {
                Text = "Enter the device IP and press Set Up.",
                Location = new Point(16, y),
                AutoSize = false,
                Size = new Size(600, 20),
                ForeColor = Color.FromArgb(200, 200, 200)
            };
            Controls.Add(_status);
            y += 26;

            _log = new TextBox
            {
                Location = new Point(16, y),
                Size = new Size(604, 240),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(190, 255, 190),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9)
            };
            Controls.Add(_log);
            y += 250;

            _done = new Button
            {
                Text = "Done",
                Location = new Point(524, y),
                Size = new Size(96, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White,
                Enabled = false,
                FlatAppearance = { BorderColor = Color.FromArgb(90, 90, 90) }
            };
            _done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_done);

            _ip.Text = string.IsNullOrEmpty(prefillHost) ? "" : prefillHost;
            _user.Text = string.IsNullOrEmpty(prefillUser) ? "root" : prefillUser;
            _port.Text = prefillPort > 0 ? prefillPort.ToString() : "22";
            for (int i = 0; i < OsNames.Length; i++)
            {
                if (OsKeyMatches(i, prefillOs)) { _os.SelectedIndex = i; break; }
            }
        }

        private static bool OsKeyMatches(int index, string key)
        {
            if (string.IsNullOrEmpty(key)) return index == 0;
            string[] names = { "", "knulli", "batocera", "arkos", "amberelec", "rocknix", "muos", "generic" };
            return index < names.Length && names[index] == key.ToLowerInvariant();
        }

        /// <summary>OS dropdown change: prefill the SSH user/password defaults for that OS.</summary>
        private void OnOsChanged(object sender, EventArgs e)
        {
            string key = null;
            switch (_os.SelectedIndex)
            {
                case 1: key = "knulli"; break;
                case 2: key = "batocera"; break;
                case 3: key = "arkos"; break;
                case 4: key = "amberelec"; break;
                case 5: key = "rocknix"; break;
                case 6: key = "muos"; break;
                case 7: key = "generic"; break;
            }
            if (key == null) return;
            string[] d;
            if (SshDefaults.TryGetValue(key, out d))
            {
                if (d[0].Length > 0) _user.Text = d[0];
                if (d[1].Length > 0) _password.Text = d[1];
            }
        }

        private Label AddLabel(string text, int x, int y, int width)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = false,
                Size = new Size(width, 20),
                ForeColor = Color.FromArgb(200, 200, 200)
            };
            Controls.Add(l);
            return l;
        }

        private TextBox AddBox(int x, int y, int width)
        {
            var t = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 22),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(t);
            return t;
        }

        private void AppendLog(string line)
        {
            if (IsDisposed) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "fv_wizard.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch { }
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLog), line); } catch { }
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool>(SetBusy), busy);
                return;
            }
            _go.Enabled = !busy;
            _test.Enabled = !busy;
        }

        private void SetStatus(string text, Color color)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, Color>(SetStatus), text, color);
                return;
            }
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void EnableDone()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(EnableDone));
                return;
            }
            _done.Enabled = true;
            _done.Focus();
        }

        private void OnTestClick(object sender, EventArgs e)
        {
            string host = _ip.Text.Trim();
            string user = _user.Text.Trim();
            int port = SshPort;
            string pw = _password.Text;
            if (pw.Length == 0) pw = null;
            SetBusy(true);
            SetStatus("Testing SSH connection ...", Color.FromArgb(200, 200, 200));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DeviceSetup.TestConnection(host, user, port, pw, AppendLog);
                    SetStatus("Connection OK - device is ready for setup.", Color.LimeGreen);
                }
                catch (DeviceSetup.SetupException ex)
                {
                    AppendLog("FAILED: " + ex.Message);
                    SetStatus("Connection failed - see log.", Color.OrangeRed);
                }
                catch (Exception ex)
                {
                    AppendLog("FAILED (unexpected): " + ex.Message);
                    SetStatus("Connection failed - see log.", Color.OrangeRed);
                }
                finally
                {
                    SetBusy(false);
                }
            });
        }

        private void OnGoClick(object sender, EventArgs e)
        {
            string host = _ip.Text.Trim();
            string user = _user.Text.Trim();
            int port = SshPort;
            string pw = _password.Text;
            if (pw.Length == 0) pw = null;

            string err;
            if (!DeviceSetup.ValidateHost(host, out err))
            {
                SetStatus(err, Color.OrangeRed);
                return;
            }

            SetBusy(true);
            SetStatus("Working - probe, deploy, start and verify ...", Color.FromArgb(200, 200, 200));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                DeviceSetup.Result r = null;
                try
                {
                    r = DeviceSetup.Run(host, user, port, pw);
                    foreach (string line in r.Log) AppendLog(line);
                    if (r.Ok)
                    {
                        Host = r.Host;
                        SetStatus("Device ready - press Done (the viewer will auto-connect).", Color.LimeGreen);
                        EnableDone();
                    }
                    else
                    {
                        SetStatus("Setup failed - see log.", Color.OrangeRed);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("FAILED (unexpected): " + ex.Message);
                    SetStatus("Setup failed - see log.", Color.OrangeRed);
                }
                finally
                {
                    SetBusy(false);
                }
            });
        }
    }
}
