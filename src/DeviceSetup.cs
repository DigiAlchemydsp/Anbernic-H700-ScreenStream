using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FatmaVision
{
    /// <summary>
    /// Device-side deployment core ("set it and forget it"): probes the
    /// handheld over SSH, identifies the platform (Knulli, Batocera, ArkOS,
    /// AmberELEC, ROCKNIX, muOS or generic Linux), deploys or repairs
    /// stream.sh + ports-screenstream.sh, (re)starts the stream on :5555 and
    /// verifies it end-to-end with the built-in decoder. Used by the Device
    /// Setup Wizard (GUI) and the --setup-device CLI flag.
    ///
    /// Multi-platform model: probe, don't deny. A device is accepted when it
    /// has a /dev/fb0 framebuffer, an ffmpeg with an H.264 encoder and a
    /// writable storage area. The install/ports paths adapt to the detected
    /// platform.
    ///
    /// Architecture note: the device is the SERVER (listens on tcp:5555);
    /// the IP pair is saved on the client side, so stream.sh needs no target
    /// IP baked in.
    /// </summary>
    public class DeviceSetup
    {
        public class Result
        {
            public bool Ok;
            public string Host;
            public string StreamVersion = "unknown";
            public readonly List<string> Log = new List<string>();
        }

        // canonical device scripts, base64 (keep in sync with device/*.sh in the repo)
        internal const string StreamShB64 =
            "IyEvYmluL3NoCiMgc2NyZWVuc3RyZWFtIC0gZmIwIC0+IHJhdyBILjI2NCBFUyAtPiBwYWNlZCBkZWxpdmVyeSBvdmVyIFRDUCAocmVhZC1vbmx5LCBubwojIHR1bmluZ3MsIG5vIG11eGluZyBvbiB0aGUgZGV2aWNlKS4gc3RhZ2UxIGVuY29kZXMgYXMgZmFzdCBhcyBwb3NzaWJsZSB0byBhCiMgcGlwZTsgc3RhZ2UyIC1yZSBwYWNlcyBkZWxpdmVyeSB0byByZWFsIHRpbWUuIE9uZSBjbGllbnQgcGVyIGNvbm5lY3Rpb24uClBPUlQ9IiR7MTotNTU1NX0iCkRJUj0iJChjZCAiJChkaXJuYW1lICIkMCIpIiAyPi9kZXYvbnVsbCAmJiBwd2QpIgpGRk1QRUc9IiQoY29tbWFuZCAtdiBmZm1wZWcgMj4vZGV2L251bGwpIgpbIC14ICIkRElSL2ZmbXBlZyIgXSAmJiBGRk1QRUc9IiRESVIvZmZtcGVnIgppZiBbIC16ICIkRkZNUEVHIiBdOyB0aGVuCiAgZWNobyAic2NyZWVuc3RyZWFtOiBubyBmZm1wZWcgYXZhaWxhYmxlIiA+JjIKICBleGl0IDEKZmkKCiMgTm8gZnJvbnRlbmQgd2F0Y2hkb2c6IHdoZW4gYSBnYW1lL2FwcCBsYXVuY2hlcyBpdCBpbnRlbnRpb25hbGx5IHN0b3BzIHRoZQojIGZyb250ZW5kIChFUy9tdU9TKSB0byB5aWVsZCB0aGUgZGlzcGxheTsgdGhlIHN0cmVhbSBtdXN0IGtlZXAgZW5jb2RpbmcgZmIwCiMgd2l0aG91dCBldmVyIHN0YXJ0aW5nIGEgZHVwbGljYXRlIGZyb250ZW5kIHNlc3Npb24uCkZQUz0iJHtTQ1JFRU5TVFJFQU1fRlBTOi0zMH0iCndoaWxlIHRydWU7IGRvCiAgIiRGRk1QRUciIC1oaWRlX2Jhbm5lciAtbG9nbGV2ZWwgd2FybmluZyBcCiAgICAtZiBmYmRldiAtZnJhbWVyYXRlICIkRlBTIiAtaSAvZGV2L2ZiMCBcCiAgICAtdmYgInNldHRiPUFWVEIsc2V0cHRzPU4vJHtGUFN9L1RCLHJlYWx0aW1lLGZwcz0ke0ZQU30sZm9ybWF0PXl1djQyMHAiIFwKICAgIC1jOnYgbGlieDI2NCAtcHJlc2V0IHVsdHJhZmFzdCAtdHVuZSB6ZXJvbGF0ZW5jeSAtYmYgMCAtZyAxMiAtciAiJEZQUyIgXAogICAgLXByb2ZpbGU6diBiYXNlbGluZSAtcGl4X2ZtdCB5dXY0MjBwIFwKICAgIC1mIGgyNjQgLSAyPi90bXAvc2NyZWVuc3RyZWFtX3N0YWdlMS5sb2cgfAogICIkRkZNUEVHIiAtaGlkZV9iYW5uZXIgLWxvZ2xldmVsIHdhcm5pbmcgXAogICAgLXJlIC1mZmxhZ3Mgbm9idWZmZXIgLWYgaDI2NCAtaSAtIC1jOnYgY29weSBcCiAgICAtZiBoMjY0ICJ0Y3A6Ly8wLjAuMC4wOiR7UE9SVH0/bGlzdGVuPTEiCiAgc2xlZXAgMQpkb25lCg==";
        internal const string PortsLauncherShB64 =
            "IyEvYmluL3NoCiMgU2NyZWVuU3RyZWFtIFBvcnRzIGVudHJ5IChLbnVsbGkvQmF0b2NlcmEvQXJrT1MvQW1iZXJFTEVDL1JPQ0tOSVgpIC0gZmIwCiMgSC4yNjQgdGNwOjU1NTUuIEZpcnN0IGxhdW5jaCBzdGFydHMgdGhlIHN0cmVhbTsgbGF1bmNoaW5nIGFnYWluIHN0b3BzIGl0LgojIFJlYWQtb25seSwgbm8gdHVuaW5ncy4KVlBJREY9L3Zhci9ydW4vc2NyZWVuc3RyZWFtLnBpZApWRU5HPSIiCmZvciBjIGluICIkKGRpcm5hbWUgIiQwIikvc3RyZWFtLnNoIiAvdXNlcmRhdGEvc2NyZWVuc3RyZWFtL3N0cmVhbS5zaCBcCiAgICAgICAgIC9zdG9yYWdlL3NjcmVlbnN0cmVhbS9zdHJlYW0uc2ggL3JvbXMyL3NjcmVlbnN0cmVhbS9zdHJlYW0uc2ggXAogICAgICAgICAvcm9tcy9zY3JlZW5zdHJlYW0vc3RyZWFtLnNoOyBkbwogIFsgLWYgIiRjIiBdICYmIFZFTkc9IiRjIiAmJiBicmVhawpkb25lCmlmIFsgLXogIiRWRU5HIiBdOyB0aGVuCiAgZWNobyAic3RyZWFtLnNoIG5vdCBmb3VuZCAtIHJ1biB0aGUgRmF0bWFWaXNpb24gU2V0dXAgV2l6YXJkIGZpcnN0IgogIHNsZWVwIDQKICBleGl0IDEKZmkKCmVjaG8gIj09PT09IFNDUkVFTlNUUkVBTSA9PT09PSIKZWNobyAidmlkZW86IGZiMCAtPiBILjI2NCB0Y3A6NTU1NSIKCmlmIFsgLWYgIiRWUElERiIgXSAmJiBraWxsIC0wICIkKGNhdCAiJFZQSURGIikiIDI+L2Rldi9udWxsOyB0aGVuCiAgICBlY2hvICJzdHJlYW0gUlVOTklORyAtPiBzdG9wcGluZyIKICAgIGtpbGwgLVRFUk0gLSIkKGNhdCAiJFZQSURGIikiIDI+L2Rldi9udWxsCiAgICBybSAtZiAiJFZQSURGIgogICAgc2xlZXAgMgogICAgZWNobyAic3RvcHBlZC4iCmVsc2UKICAgIGVjaG8gInN0YXJ0aW5nIHZpZGVvIHN0cmVhbSBvbiBwb3J0IDU1NTUgLi4uIgogICAgc2V0c2lkIG5vaHVwIHNoICIkVkVORyIgNTU1NSA8L2Rldi9udWxsID4vdG1wL3NjcmVlbnN0cmVhbS5sb2cgMj4mMSAmCiAgICBlY2hvICQhID4gIiRWUElERiIKICAgIHNsZWVwIDMKICAgIGlmIFsgLWYgIiRWUElERiIgXSAmJiBraWxsIC0wICIkKGNhdCAiJFZQSURGIikiIDI+L2Rldi9udWxsOyB0aGVuCiAgICAgICAgZWNobyAic3RyZWFtIFVQIC0+IGNvbm5lY3Qgd2l0aCB0aGUgRmF0bWFWaXNpb24gYXBwIgogICAgZWxzZQogICAgICAgIGVjaG8gIkZBSUxFRCAtIHNlZSAvdG1wL3NjcmVlbnN0cmVhbS5sb2ciCiAgICBmaQpmaQplY2hvICJyZXR1cm5pbmcgdG8gdGhlIGZyb250ZW5kIGluIDRzIC4uLiIKc2xlZXAgNApleGl0IDAK";

        private const int StreamPort = 5555;

        private static readonly byte[] StreamShBytes = Convert.FromBase64String(StreamShB64);

        private static string CanonicalStreamMd5
        {
            get
            {
                using (MD5 m = MD5.Create())
                {
                    byte[] h = m.ComputeHash(StreamShBytes);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
        }

        public static bool ValidateHost(string host, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(host)) { error = "device IP or hostname is required"; return false; }
            string h = host.Trim();
            IPAddress ip;
            if (IPAddress.TryParse(h, out ip)) return true;
            if (h.Length <= 253)
            {
                bool ok = true;
                foreach (string part in h.Split('.'))
                {
                    if (part.Length == 0 || part.Length > 63) { ok = false; break; }
                    foreach (char c in part)
                    {
                        if (!(char.IsLetterOrDigit(c) || c == '-')) { ok = false; break; }
                    }
                    if (!ok) break;
                }
                if (ok) return true;
            }
            error = "invalid IP address or hostname: " + h;
            return false;
        }

        // ---------- platform detection (multi-platform model) ----------

        /// <summary>
        /// Everything the probe learned about the device. Key is the platform
        /// profile name (knulli/batocera/arkos/amberelec/rocknix/muos/generic).
        /// </summary>
        public class Platform
        {
            public string Key;
            public string DisplayName;
            public string OsId;
            public string OsName;
            public string OsIdLike;
            public string Arch;
            public bool Fb0;
            public string Fb0Size;                 // "640,480" or "" if unknown
            public string Ffmpeg;                  // absolute path or "" if missing
            public bool X264;
            public readonly List<string> DataDirs = new List<string>();   // writable candidates reported by probe
            public readonly List<string> PortsDirs = new List<string>();  // existing ports folders
            public readonly List<string> PortsLauncherDirs = new List<string>(); // ports folders already holding the launcher
            public bool EsInit;                    // /etc/init.d/S31emulationstation exists
            public bool EsRunning;
            public bool Muxplore;                  // muOS frontend present
            public bool ListenerUp;                // something already listens on :5555
            public string StreamDir;               // existing screenstream dir ("" if none)
            public string StreamMd5;               // md5 of existing stream.sh ("" if none)
            public string DataDir;                 // resolved install parent (null if none)
        }

        /// <summary>Single-SSH-call device probe: identity, capabilities, layout.</summary>
        private static Platform DetectPlatform(string host, string sshUser, int sshPort, string password, Action<string> log)
        {
            Platform p = new Platform();
            p.OsId = ""; p.OsName = ""; p.OsIdLike = ""; p.Arch = ""; p.Fb0Size = "";
            p.Ffmpeg = ""; p.StreamDir = ""; p.StreamMd5 = "";
            string probe =
                "echo \"OS_ID $(grep '^ID=' /etc/os-release 2>/dev/null | head -1 | cut -d= -f2 | tr -d '\\\"')\"\n" +
                "echo \"OS_NAME $(grep '^NAME=' /etc/os-release 2>/dev/null | head -1 | cut -d= -f2 | tr -d '\\\"')\"\n" +
                "echo \"OS_ID_LIKE $(grep '^ID_LIKE=' /etc/os-release 2>/dev/null | head -1 | cut -d= -f2 | tr -d '\\\"')\"\n" +
                "echo \"ARCH $(uname -m)\"\n" +
                "if [ -c /dev/fb0 ]; then echo \"FB0 $(cat /sys/class/graphics/fb0/virtual_size 2>/dev/null)\"; else echo FB0_MISSING; fi\n" +
                "FF=$(command -v ffmpeg 2>/dev/null)\n" +
                "if [ -n \"$FF\" ]; then echo \"FFMPEG $FF\"; else echo FFMPEG_MISSING; fi\n" +
                "if [ -n \"$FF\" ] && $FF -hide_banner -encoders 2>/dev/null | grep -q '264'; then echo X264_YES; else echo X264_NO; fi\n" +
                "for d in /userdata /storage /roms2 /roms /mnt/mmc/MUOS /tmp; do\n" +
                "  if [ -d \"$d\" ] && [ -w \"$d\" ]; then echo \"DATA_WRITABLE $d\"; fi\n" +
                "done\n" +
                "for d in /userdata/roms/ports /storage/roms/ports /roms2/ports /roms/ports; do\n" +
                "  if [ -d \"$d\" ]; then echo \"PORTS_DIR $d\"; if [ -f \"$d/ports-screenstream.sh\" ]; then echo \"PORTS_LAUNCHER $d\"; fi; fi\n" +
                "done\n" +
                "if [ -x /etc/init.d/S31emulationstation ]; then echo ES_INIT_YES; fi\n" +
                "if pidof emulationstation >/dev/null 2>&1; then echo FRONTEND_RUNNING_ES; fi\n" +
                "if command -v muxplore >/dev/null 2>&1; then echo FRONTEND_MUXPLORE; fi\n" +
                "for d in /userdata/screenstream /storage/screenstream /roms2/screenstream /roms/screenstream /mnt/mmc/MUOS/screenstream; do\n" +
                "  if [ -d \"$d\" ]; then echo \"STREAMDIR $d\"; fi\n" +
                "done\n" +
                "for f in /userdata/screenstream/stream.sh /storage/screenstream/stream.sh /roms2/screenstream/stream.sh /roms/screenstream/stream.sh /mnt/mmc/MUOS/screenstream/stream.sh; do\n" +
                "  if [ -f \"$f\" ]; then echo \"STREAM_MD5 $(md5sum \"$f\" | awk '{print $1}')\"; break; fi\n" +
                "done\n" +
                "if [ -f /userdata/screenstream/stream.sh ] || [ -f /storage/screenstream/stream.sh ] || [ -f /roms2/screenstream/stream.sh ] || [ -f /roms/screenstream/stream.sh ] || [ -f /mnt/mmc/MUOS/screenstream/stream.sh ]; then :; else echo STREAM_MISSING; fi\n" +
                "if ss -tln 2>/dev/null | grep -q :5555; then echo LISTENER_UP; else echo LISTENER_DOWN; fi\n";
            SshReply r = Ssh(host, sshUser, sshPort, password, probe, 30000);
            if (r.Rc != 0)
            {
                throw new SetupException("cannot reach device over SSH (rc=" + r.Rc + "). " +
                    "Check the IP, that SSH is enabled on the device, and that key auth is set up (or plink+password). " +
                    "Details: " + r.All);
            }
            foreach (string line in r.All.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0) continue;
                int sp = l.IndexOf(' ');
                string key = sp < 0 ? l : l.Substring(0, sp);
                string val = sp < 0 ? "" : l.Substring(sp + 1).Trim();
                if (key == "OS_ID") p.OsId = val;
                else if (key == "OS_NAME") p.OsName = val;
                else if (key == "OS_ID_LIKE") p.OsIdLike = val;
                else if (key == "ARCH") p.Arch = val;
                else if (key == "FB0") { p.Fb0 = true; p.Fb0Size = val; }
                else if (key == "FB0_MISSING") p.Fb0 = false;
                else if (key == "FFMPEG") p.Ffmpeg = val;
                else if (key == "FFMPEG_MISSING") p.Ffmpeg = "";
                else if (key == "X264_YES") p.X264 = true;
                else if (key == "X264_NO") p.X264 = false;
                else if (key == "DATA_WRITABLE") p.DataDirs.Add(val);
                else if (key == "PORTS_DIR") p.PortsDirs.Add(val);
                else if (key == "PORTS_LAUNCHER") p.PortsLauncherDirs.Add(val);
                else if (key == "ES_INIT_YES") p.EsInit = true;
                else if (key == "FRONTEND_RUNNING_ES") p.EsRunning = true;
                else if (key == "FRONTEND_MUXPLORE") p.Muxplore = true;
                else if (key == "STREAMDIR") p.StreamDir = val;
                else if (key == "STREAM_MD5") p.StreamMd5 = val;
                else if (key == "LISTENER_UP") p.ListenerUp = true;
                else if (key == "LISTENER_DOWN") p.ListenerUp = false;
            }
            p.Key = Classify(p);
            p.DisplayName = DisplayNameForKey(p.Key);
            p.DataDir = ResolveDataDir(p);
            if (log != null)
            {
                log("PLATFORM " + p.Key + " (" + p.DisplayName + ") arch=" + (p.Arch.Length > 0 ? p.Arch : "?") +
                    (p.Fb0 ? " fb0=" + (p.Fb0Size.Length > 0 ? p.Fb0Size : "present") : " no-fb0") +
                    " ffmpeg=" + (p.Ffmpeg.Length > 0 ? p.Ffmpeg : "missing") +
                    " x264=" + (p.X264 ? "yes" : "no") +
                    " data=" + (p.DataDir ?? "-") +
                    " frontend=" + (p.EsRunning ? "emulationstation" : p.Muxplore ? "muxplore" : "unknown"));
            }
            return p;
        }

        private static string Classify(Platform p)
        {
            string id = (p.OsId ?? "").ToLowerInvariant();
            string name = (p.OsName ?? "").ToLowerInvariant();
            string like = (p.OsIdLike ?? "").ToLowerInvariant();
            if (id.Contains("knulli") || name.Contains("knulli")) return "knulli";
            if (id.Contains("batocera") || like.Contains("batocera") || name.Contains("batocera")) return "batocera";
            if (id.Contains("amberelec") || like.Contains("amberelec") || name.Contains("amberelec")) return "amberelec";
            if (id.Contains("rocknix") || like.Contains("rocknix") || name.Contains("rocknix")) return "rocknix";
            if (id.Contains("muos") || name.Contains("muos")) return "muos";
            if (id.Contains("arkos") || name.Contains("arkos")) return "arkos";
            return "generic";
        }

        private static string DisplayNameForKey(string key)
        {
            switch (key)
            {
                case "knulli": return "Knulli";
                case "batocera": return "Batocera";
                case "amberelec": return "AmberELEC";
                case "rocknix": return "ROCKNIX";
                case "muos": return "muOS";
                case "arkos": return "ArkOS";
                default: return "Generic Linux";
            }
        }

        /// <summary>Preferred storage root per platform; generic falls back to probe results.</summary>
        private static string ResolveDataDir(Platform p)
        {
            string[] pref;
            switch (p.Key)
            {
                case "knulli":
                case "batocera": pref = new[] { "/userdata" }; break;
                case "amberelec":
                case "rocknix": pref = new[] { "/storage" }; break;
                case "arkos": pref = new[] { "/roms2", "/roms" }; break;
                case "muos": pref = new[] { "/mnt/mmc/MUOS" }; break;
                default: pref = null; break;
            }
            if (pref != null)
            {
                foreach (string d in pref)
                {
                    if (p.DataDirs.Contains(d)) return d;
                }
            }
            // preferred root not writable (or unknown platform): first writable
            // candidate reported by the probe
            if (p.DataDirs.Count > 0) return p.DataDirs[0];
            return null;
        }

        /// <summary>
        /// Capability gate: fb0 + ffmpeg/x264 + a writable storage area.
        /// </summary>
        private static void GateCapabilities(Platform p)
        {
            if (!p.Fb0)
                throw new SetupException("device has no /dev/fb0 framebuffer - FatmaVision targets Linux handhelds " +
                    "whose frontend renders to the framebuffer (Knulli, Batocera, ArkOS, AmberELEC, ROCKNIX, muOS). " +
                    "If this IS a handheld, its firmware may need a framebuffer console enabled.");
            if (p.Ffmpeg.Length == 0 || !p.X264)
                throw new SetupException("device has no ffmpeg with an H.264 encoder - this platform is not supported yet " +
                    "(a bundled static ffmpeg deploy is on the roadmap).");
            if (p.DataDir == null)
                throw new SetupException("no writable storage area found on the device (checked /userdata /storage /roms2 /roms /mnt/mmc/MUOS /tmp)");
        }

        // ---------- SSH transport ----------

        private static string _sshPath = null;

        private static string FindSsh()
        {
            if (_sshPath != null) return _sshPath.Length > 0 ? _sshPath : null;
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
            if (File.Exists(sys)) { _sshPath = sys; return sys; }
            try
            {
                string where = RunLocal("where", "ssh.exe", 5000);
                if (where != null && where.Trim().Length > 0)
                {
                    _sshPath = where.Trim().Split('\n')[0].Trim();
                    return _sshPath;
                }
            }
            catch { }
            _sshPath = "";
            return null;
        }

        private static bool FindPlink()
        {
            try
            {
                string w = RunLocal("where", "plink.exe", 5000);
                return w != null && w.Trim().Length > 0;
            }
            catch { return false; }
        }

        private static string RunLocal(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return null;
                }
                return outp;
            }
        }

        private class SshReply
        {
            public int Rc;
            public string Out;
            public string Err;
            public string All { get { return (Out + "\n" + Err).Trim(); } }
        }

        /// <summary>
        /// Runs the given shell script on the device via ssh (key auth,
        /// BatchMode) feeding the script over stdin - no remote quoting issues.
        /// Falls back to plink -pw when key auth fails, a password is given
        /// and plink is on PATH.
        /// </summary>
        private static SshReply Ssh(string host, string user, int port, string password, string script, int timeoutMs)
        {
            string ssh = FindSsh();
            if (ssh == null)
            {
                SshReply r0 = new SshReply();
                r0.Rc = -1;
                r0.Err = "OpenSSH client (ssh.exe) not found - install the Windows OpenSSH Client feature";
                return r0;
            }
            string target = user + "@" + host;
            string common = "-o BatchMode=yes -o ConnectTimeout=6 -o StrictHostKeyChecking=accept-new -p " + port + " " + target + " sh -s";
            SshReply r = RunTransport(ssh, common, script, timeoutMs);

            if (r.Rc != 0 && !string.IsNullOrEmpty(password) && FindPlink())
            {
                string plinkArgs = "-batch -P " + port + " -pw \"" + password.Replace("\"", "") + "\" " + target + " sh -s";
                SshReply p = RunTransport("plink.exe", plinkArgs, script, timeoutMs);
                if (p.Rc == 0) return p;
                r.Err += " | plink fallback: " + p.All;
            }
            return r;
        }

        private static SshReply RunTransport(string exe, string args, string script, int timeoutMs)
        {
            SshReply r = new SshReply();
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                try { p.Start(); }
                catch (Exception ex)
                {
                    r.Rc = -1;
                    r.Err = ex.Message;
                    return r;
                }
                StreamWriter stdin = p.StandardInput;
                stdin.Write(script);
                stdin.Close();
                string outp = null, errp = null;
                try { outp = p.StandardOutput.ReadToEnd(); } catch { }
                try { errp = p.StandardError.ReadToEnd(); } catch { }
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    r.Rc = -1;
                    r.Out = outp ?? "";
                    r.Err = (errp ?? "") + " (timed out after " + timeoutMs / 1000 + " s)";
                    return r;
                }
                r.Rc = p.ExitCode;
                r.Out = outp ?? "";
                r.Err = errp ?? "";
            }
            return r;
        }

        // ---------- the wizard pipeline ----------

        public static Result Run(string host, string sshUser, int sshPort, string password)
        {
            Result res = new Result();
            res.Host = host.Trim();
            Action<string> log = s => res.Log.Add(s);
            try
            {
                DoAll(host.Trim(), sshUser, sshPort, password, log);
                res.Ok = true;
            }
            catch (SetupException ex)
            {
                log("FAILED: " + ex.Message);
                res.Ok = false;
            }
            catch (Exception ex)
            {
                log("FAILED (unexpected): " + ex.ToString());
                res.Ok = false;
            }
            return res;
        }

        public class SetupException : Exception
        {
            public SetupException(string msg) : base(msg) { }
        }

        /// <summary>Connectivity + platform check only (wizard "Test SSH").</summary>
        public static void TestConnection(string host, string sshUser, int sshPort, string password, Action<string> log)
        {
            string err;
            if (!ValidateHost(host, out err)) throw new SetupException(err);
            if (sshPort <= 0 || sshPort > 65535) throw new SetupException("invalid SSH port");
            log("connecting to " + host + ":" + sshPort + " as " + sshUser + " ...");
            Platform p = DetectPlatform(host, sshUser, sshPort, password, log);
            if (!p.Fb0)
                throw new SetupException("device is reachable but has no /dev/fb0 framebuffer - not a supported " +
                    "handheld streaming target (Knulli, Batocera, ArkOS, AmberELEC, ROCKNIX, muOS)");
            log("OK - " + p.DisplayName + " reachable over SSH (" +
                (p.Arch.Length > 0 ? p.Arch + ", " : "") + "fb0 " + (p.Fb0Size.Length > 0 ? p.Fb0Size : "present") + ")");
        }

        private static void DoAll(string host, string sshUser, int sshPort, string password, Action<string> log)
        {
            // Step 1 - validate inputs
            string err;
            if (!ValidateHost(host, out err)) throw new SetupException(err);
            if (sshPort <= 0 || sshPort > 65535) throw new SetupException("invalid SSH port");

            // Step 2 - connection + platform probe + capability gate
            log("[1/5] Connecting to " + host + ":" + sshPort + " as " + sshUser + " ...");
            Platform p = DetectPlatform(host, sshUser, sshPort, password, log);
            log("      " + p.DisplayName + " detected" +
                (p.Fb0 ? " - fb0 " + (p.Fb0Size.Length > 0 ? p.Fb0Size : "present") : " - NO fb0") +
                (p.StreamMd5.Length > 0 ? " - stream.sh present" : " - no stream installed yet"));
            GateCapabilities(p);
            string ssdir = p.DataDir + "/screenstream";

            // Step 3 - deploy or repair
            string canonicalMd5 = CanonicalStreamMd5;
            bool found = p.StreamMd5.Length > 0;
            if (found && p.StreamMd5 == canonicalMd5)
            {
                log("[2/5] stream.sh already canonical (md5 " + p.StreamMd5 + ") - nothing to deploy");
                List<string> missingLauncher = new List<string>();
                foreach (string pd in p.PortsDirs)
                {
                    if (!p.PortsLauncherDirs.Contains(pd)) missingLauncher.Add(pd);
                }
                if (missingLauncher.Count > 0)
                {
                    log("      ports launcher missing in " + string.Join(", ", missingLauncher.ToArray()) + " - installing");
                    CopyPortsLauncher(host, sshUser, sshPort, password, ssdir, missingLauncher, log);
                }
            }
            else
            {
                string reason = found ? "stream.sh differs from canonical (found " + p.StreamMd5 + ")" : "no stream.sh found";
                log("[2/5] Deploying stream.sh + ports launcher to " + ssdir + " (" + reason + ") ...");
                DeployScripts(host, sshUser, sshPort, password, ssdir, p.PortsDirs, log);
            }
            if (p.StreamDir.Length == 0)
                log("      note: " + ssdir + " will be created");

            // Step 4 - (re)start the stream and verify the listener
            log("[3/5] (Re)starting stream on port " + StreamPort + " ...");
            StartListener(host, sshUser, sshPort, password, ssdir, log);

            // Step 5 - end-to-end verify with the built-in decoder
            log("[4/5] Verifying stream end-to-end (decoding 30 frames) ...");
            int decoded, demuxed;
            long bytes;
            bool ok = SelfTest.RunCore(host, StreamPort, 30, 30, log, out decoded, out demuxed, out bytes);
            if (!ok)
                throw new SetupException("end-to-end check failed - stream up but no frames decoded");
            log("[5/5] verified: " + decoded + "/30 frames decoded, " + demuxed + " demuxed, " + bytes + " bytes");
            log("DONE - device is ready; connect with FatmaVision " + host + " " + StreamPort);
        }

        /// <summary>
        /// "Set and forget" daily path: makes sure the device is streaming on
        /// :5555 without touching the device by hand. Deploys the canonical
        /// stream.sh if it is missing/damaged, starts the stream if it is down,
        /// and leaves it alone when it is already running. Throws SetupException
        /// when the device is unreachable or refuses to start.
        /// </summary>
        public static void EnsureStream(string host, string sshUser, int sshPort, string password, Action<string> log)
        {
            string err;
            if (!ValidateHost(host, out err)) throw new SetupException(err);
            if (sshPort <= 0 || sshPort > 65535) throw new SetupException("invalid SSH port");

            Platform p = DetectPlatform(host, sshUser, sshPort, password, log);
            if (p.ListenerUp)
            {
                log("device stream already running on :" + StreamPort);
                return;
            }
            GateCapabilities(p);
            string ssdir = p.DataDir + "/screenstream";

            string canonicalMd5 = CanonicalStreamMd5;
            if (p.StreamMd5.Length == 0 || p.StreamMd5 != canonicalMd5)
            {
                log("device stream down; deploying stream.sh to " + ssdir + " ...");
                DeployScripts(host, sshUser, sshPort, password, ssdir, p.PortsDirs, log);
            }
            else
            {
                log("device stream down; starting it ...");
            }
            StartListener(host, sshUser, sshPort, password, ssdir, log);
        }

        /// <summary>
        /// Stops the screenstream on the device: terminates stream.sh, then
        /// kills anything still listening on :5555. Used when the client
        /// disconnects. Platform-independent.
        /// </summary>
        public static void StopStream(string host, string sshUser, int sshPort, string password, Action<string> log)
        {
            string err;
            if (!ValidateHost(host, out err)) throw new SetupException(err);
            string stop =
                "P=$(ps -e -o pid,args 2>/dev/null | grep '[s]tream.sh' | awk '{print $1}'); " +
                "if [ -n \"$P\" ]; then kill -TERM $P 2>/dev/null; sleep 1; fi\n" +
                "PIDS=$(ss -tlnp 2>/dev/null | grep :5555 | grep -o 'pid=[0-9]*' | grep -o '[0-9]*'); " +
                "if [ -n \"$PIDS\" ]; then kill -9 $PIDS 2>/dev/null; sleep 2; fi\n" +
                "PIDS2=$(ss -tlnp 2>/dev/null | grep :5555 | grep -o 'pid=[0-9]*' | grep -o '[0-9]*'); " +
                "if [ -n \"$PIDS2\" ]; then kill -9 $PIDS2 2>/dev/null; sleep 1; fi\n" +
                "rm -f /var/run/screenstream.pid\n" +
                "if ss -tln 2>/dev/null | grep -q :5555; then echo STILL_LISTENING; else echo STREAM_STOPPED; fi\n";
            SshReply r = Ssh(host, sshUser, sshPort, password, stop, 20000);
            if (r.Rc != 0)
                throw new SetupException("stop failed (rc=" + r.Rc + "): " + r.All);
            if (r.All.IndexOf("STREAM_STOPPED") < 0)
                log("warning: something still listens on :5555");
            else
                log("device stream stopped");
        }

        // ---------- shared device operations ----------

        private static void DeployScripts(string host, string sshUser, int sshPort, string password, string ssdir, List<string> portsDirs, Action<string> log)
        {
            string copy = "";
            foreach (string pd in portsDirs)
                copy += "if [ -d '" + pd + "' ]; then cp '" + ssdir + "/ports-screenstream.sh' '" + pd + "/' 2>/dev/null; echo PORTS_COPIED " + pd + "; fi\n";
            string deploy =
                "mkdir -p '" + ssdir + "'\n" +
                "echo '" + StreamShB64 + "' | base64 -d > '" + ssdir + "/stream.sh'\n" +
                "echo '" + PortsLauncherShB64 + "' | base64 -d > '" + ssdir + "/ports-screenstream.sh'\n" +
                "chmod +x '" + ssdir + "/stream.sh' '" + ssdir + "/ports-screenstream.sh'\n" +
                copy +
                "md5sum '" + ssdir + "/stream.sh'\n";
            SshReply dep = Ssh(host, sshUser, sshPort, password, deploy, 30000);
            if (dep.Rc != 0) throw new SetupException("deploy failed (rc=" + dep.Rc + "): " + dep.All);
            log("      " + dep.All.Replace("\n", " | "));
            if (dep.All.IndexOf(CanonicalStreamMd5) < 0)
                throw new SetupException("deployed file md5 does not match canonical - aborting");
        }

        private static void CopyPortsLauncher(string host, string sshUser, int sshPort, string password, string ssdir, List<string> portsDirs, Action<string> log)
        {
            string copy = "";
            foreach (string pd in portsDirs)
                copy += "if [ -d '" + pd + "' ]; then cp '" + ssdir + "/ports-screenstream.sh' '" + pd + "/' 2>/dev/null; echo PORTS_COPIED " + pd + "; fi\n";
            SshReply c2 = Ssh(host, sshUser, sshPort, password, copy, 30000);
            if (c2.Rc != 0) log("      WARNING: ports launcher copy failed: " + c2.All);
        }

        private static void StartListener(string host, string sshUser, int sshPort, string password, string ssdir, Action<string> log)
        {
            string start =
                "PIDS=$(ss -tlnp 2>/dev/null | grep :5555 | grep -o 'pid=[0-9]*' | grep -o '[0-9]*'); " +
                "if [ -n \"$PIDS\" ]; then kill -9 $PIDS 2>/dev/null; sleep 1; fi\n" +
                "rm -f /var/run/screenstream.pid\n" +
                "cd '" + ssdir + "' && (setsid sh stream.sh 5555 > stream.log 2>&1 &)\n" +
                "sleep 3\n" +
                "if ss -tln 2>/dev/null | grep -q :5555; then echo LISTENING_OK; else echo NOT_LISTENING; tail -5 '" + ssdir + "/stream.log' 2>/dev/null; tail -3 /tmp/screenstream_stage1.log 2>/dev/null; fi\n";
            SshReply st = Ssh(host, sshUser, sshPort, password, start, 45000);
            if (st.Rc != 0) throw new SetupException("stream start failed (rc=" + st.Rc + "): " + st.All);
            if (st.All.IndexOf("LISTENING_OK") < 0)
                throw new SetupException("stream did not start listening:\n" + st.All);
            log("device is streaming on :" + StreamPort);
        }
    }
}
