using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace FatmaVision
{
    /// <summary>
    /// Connects to the device's screen-stream launch script (stream.sh: fb0 ->
    /// raw H.264 Annex-B elementary stream over TCP) and demuxes the byte
    /// stream into complete frames (Annex-B, 4-byte start codes, SPS/PPS kept
    /// in-band with the IDR they precede). Auto-reconnects - the device script
    /// loops and accepts one client at a time.
    /// </summary>
    public class AnnexBClient
    {
        public event Action<byte[]> FrameReady;
        public event Action<string> StatusChanged;
        public event Action Reconnected;

        private readonly string _host;
        private readonly int _port;
        private Thread _thread;
        private volatile bool _stop;
        private Socket _sock;

        private Stream _recordSink;
        private readonly object _recordLock = new object();

        private long _bytesTotal;
        private long _framesTotal;
        private long _flushVcl;
        private long _flushSeq;

        // Demux state (only touched by the worker thread)
        private byte[] _nal = new byte[4096];
        private int _nalLen;
        private byte[] _frame = new byte[262144];
        private int _frameLen;
        private bool _frameHasVcl;
        private int _zeroRun;
        private bool _inNal;

        public AnnexBClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public long BytesTotal { get { return Interlocked.Read(ref _bytesTotal); } }
        public long FramesTotal { get { return Interlocked.Read(ref _framesTotal); } }
        public long FlushVclCount { get { return Interlocked.Read(ref _flushVcl); } }
        public long FlushSeqCount { get { return Interlocked.Read(ref _flushSeq); } }

        public void SetRecordSink(Stream s)
        {
            lock (_recordLock) { _recordSink = s; }
        }

        public void Start()
        {
            _stop = false;
            _thread = new Thread(Worker);
            _thread.IsBackground = true;
            _thread.Name = "screenstream-tcp";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            CloseSocket();
            if (_thread != null && _thread.IsAlive) _thread.Join(1500);
        }

        private void CloseSocket()
        {
            try
            {
                Socket s = _sock;
                if (s != null) { s.Close(); }
            }
            catch { }
        }

        private void Worker()
        {
            while (!_stop)
            {
                try
                {
                    RunOnce();
                }
                catch (Exception ex)
                {
                    if (!_stop) EmitStatus("error: " + ex.Message);
                }
                CloseSocket();
                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
            }
        }

        private void RunOnce()
        {
            EmitStatus("connecting " + _host + ":" + _port + " ...");
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _sock = s;
            IAsyncResult ar = s.BeginConnect(_host, _port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(5000) || _stop)
            {
                CloseSocket();
                return;
            }
            s.EndConnect(ar); // throws SocketException on refusal
            s.ReceiveTimeout = 3000;
            EmitStatus("connected - waiting for H.264 stream");
            ResetDemux();
            if (Reconnected != null) Reconnected();

            byte[] buf = new byte[65536];
            long startMs = Environment.TickCount;
            while (!_stop)
            {
                int n;
                try
                {
                    n = s.Receive(buf);
                }
                catch (SocketException se)
                {
                    if (_stop || se.SocketErrorCode == SocketError.TimedOut) continue;
                    EmitStatus("receive socket error: " + se.SocketErrorCode);
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    EmitStatus("receive error: " + ex);
                    throw;
                }
                if (n == 0)
                {
                    EmitStatus("stream closed after " + (Environment.TickCount - startMs) +
                        " ms, bytes=" + Interlocked.Read(ref _bytesTotal));
                    break;
                }
                Feed(buf, n);
                Interlocked.Add(ref _bytesTotal, n);
                Tee(buf, n);
            }
            EmitStatus("stream closed - reconnecting ...");
        }

        private void Tee(byte[] buf, int count)
        {
            Stream r;
            lock (_recordLock) { r = _recordSink; }
            if (r == null) return;
            try { r.Write(buf, 0, count); }
            catch { }
        }

        private void ResetDemux()
        {
            _nalLen = 0;
            _frameLen = 0;
            _frameHasVcl = false;
            _zeroRun = 0;
            _inNal = false;
        }

        // ---- Annex-B byte-level state machine ----
        // NAL boundaries are 00 00 01 / 00 00 00 01 start codes; emulation
        // prevention bytes mean payloads never contain them.
        private void Feed(byte[] data, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = data[i];
                if (!_inNal)
                {
                    if (b == 0) { _zeroRun++; }
                    else if (b == 1 && _zeroRun >= 2)
                    {
                        // start code found, NAL begins after it
                        CompleteNalIfAny();
                        _nalLen = 0;
                        _zeroRun = 0;
                        _inNal = true;
                    }
                    else
                    {
                        AppendZeros();
                        _zeroRun = 0;
                        _inNal = true;
                        AppendNal(b);
                    }
                }
                else
                {
                    if (b == 0) { _zeroRun++; }
                    else if (b == 1 && _zeroRun >= 2)
                    {
                        CompleteNalIfAny();
                        _nalLen = 0;
                        _zeroRun = 0;
                    }
                    else
                    {
                        AppendZeros();
                        _zeroRun = 0;
                        AppendNal(b);
                    }
                }
            }
        }

        private void AppendNal(byte b)
        {
            if (_nalLen == _nal.Length) Grow(ref _nal);
            _nal[_nalLen++] = b;
        }

        /// <summary>
        /// Zero bytes are buffered in _zeroRun to detect start codes; payload
        /// zeros (emulation-prevented, i.e. not part of 00 00 01) must be
        /// re-inserted into the NAL.
        /// </summary>
        private void AppendZeros()
        {
            while (_zeroRun > 0)
            {
                if (_nalLen == _nal.Length) Grow(ref _nal);
                _nal[_nalLen++] = 0;
                _zeroRun--;
            }
        }

        private void CompleteNalIfAny()
        {
            if (_nalLen == 0) return;
            int type = _nal[0] & 0x1F;
            bool isVcl = type >= 1 && type <= 5;
            if (isVcl)
            {
                bool sliceStarts = IsSliceStart(_nal, _nalLen);
                if (sliceStarts && _frameHasVcl)
                {
                    FlushFrame();
                    Interlocked.Increment(ref _flushVcl);
                }
                _frameHasVcl = true;
            }
            else if (type == 7 || type == 8)
            {
                // SPS/PPS: new sequence - close the previous access unit
                // so the keyframe keeps its SPS/PPS lead-in
                if (_frameHasVcl)
                {
                    FlushFrame();
                    Interlocked.Increment(ref _flushSeq);
                }
            }

            if (_frameLen + 4 + _nalLen > _frame.Length)
            {
                int want = _frameLen + 4 + _nalLen;
                int cap = _frame.Length;
                while (cap < want) cap *= 2;
                Array.Resize(ref _frame, cap);
            }
            _frame[_frameLen++] = 0;
            _frame[_frameLen++] = 0;
            _frame[_frameLen++] = 0;
            _frame[_frameLen++] = 1;
            Buffer.BlockCopy(_nal, 0, _frame, _frameLen, _nalLen);
            _frameLen += _nalLen;
            _nalLen = 0;
        }

        /// <summary>
        /// True if this VCL NAL is the first slice of a new access unit.
        /// Parses first_mb_in_slice (Exp-Golomb, bit-level - entropy mode
        /// independent). Sliced-threaded x264 (ultrafast/zerolatency, as used
        /// by the device) emits multiple slices per frame, so a naive
        /// "second VCL NAL = new frame" rule fragments every frame.
        /// </summary>
        private static bool IsSliceStart(byte[] nal, int len)
        {
            if (len < 3) return true; // degenerate: treat as a boundary
            int bit = 8;              // after the NAL header byte
            int zeros = 0;
            while (true)
            {
                int v = GetBit(nal, bit, len);
                if (v < 0) return true;
                bit++;
                if (v == 1) break;
                zeros++;
                if (zeros > 12) return false; // implausible for 640x480-class
            }
            long mb = (1L << zeros) - 1;
            for (int i = 0; i < zeros; i++)
            {
                int v = GetBit(nal, bit, len);
                if (v < 0) return true;
                bit++;
                mb += (long)v << (zeros - 1 - i);
            }
            return mb == 0;
        }

        private static int GetBit(byte[] b, int bitPos, int byteLen)
        {
            int idx = bitPos >> 3;
            if (idx >= byteLen) return -1;
            return (b[idx] >> (7 - (bitPos & 7))) & 1;
        }

        private void FlushFrame()
        {
            if (_frameLen == 0) return;
            byte[] outFrame = new byte[_frameLen];
            Buffer.BlockCopy(_frame, 0, outFrame, 0, _frameLen);
            _frameLen = 0;
            _frameHasVcl = false;
            Interlocked.Increment(ref _framesTotal);
            Action<byte[]> h = FrameReady;
            if (h != null) h(outFrame);
        }

        private void EmitStatus(string msg)
        {
            Action<string> h = StatusChanged;
            if (h != null) h(msg);
        }

        private static void Grow(ref byte[] arr)
        {
            Array.Resize(ref arr, arr.Length * 2);
        }
    }
}
