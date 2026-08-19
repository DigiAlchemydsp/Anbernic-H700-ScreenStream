using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FatmaVision
{
    /// <summary>
    /// Decodes Annex-B H.264 frames with the Windows built-in decoder MFT
    /// (Microsoft H264 Video Decoder MFT, msmpeg2vdec.dll). Owns a dedicated
    /// thread: ProcessInput / ProcessOutput loop, format-change handling,
    /// NV12 -> BGRA32 conversion and a reusable Bitmap handed to the UI.
    /// Video only - the device stream carries no audio.
    /// </summary>
    public class MfDecoder
    {
        public event Action<int, int> FrameReady;
        public event Action<string> StatusChanged;

        // device geometry (also the input type's MF_MT_FRAME_SIZE - msmpeg2vdec
        // needs it on the input type, else ProcessOutput fails with E_FAIL)
        private const long W = 640, H = 480;

        private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
        private readonly ManualResetEventSlim _more = new ManualResetEventSlim(false);
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _resetRequested;
        private long _framesDecoded;
        private int _outStarted;

        // decode-thread-only state
        private IMFTransform _mft;
        private int _w, _h, _stride;
        private long _ts;
        private byte[] _yPlane, _uvPlane;
        private int[] _bgra;

        // double-buffered output: the decode thread writes into _bmp[_cur] and
        // publishes it under _publishLock; the UI draws _published under the
        // same lock, so DrawImage can never race a LockBits on the same bitmap.
        private readonly object _publishLock = new object();
        private Bitmap[] _bmp = new Bitmap[2];
        private int _cur;
        private volatile Bitmap _published;

        public object PublishLock { get { return _publishLock; } }
        public Bitmap PublishedFrame { get { return _published; } }

        private static class DeviceGeometry
        {
            public static readonly ulong FrameSize = (ulong)(H | (W << 32));
        }

        public MfDecoder()
        {
            _bgra = new int[0];
        }

        public long FramesDecoded { get { return Interlocked.Read(ref _framesDecoded); } }

        public void Start()
        {
            _stop = false;
            _resetRequested = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "screenstream-decode";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _more.Set();
            if (_thread != null && _thread.IsAlive) _thread.Join(2000);
        }

        /// <summary>Drop queued frames and rebuild the MFT (new stream session).</summary>
        public void Reset()
        {
            byte[] d;
            while (_queue.TryDequeue(out d)) { }
            _resetRequested = true;
            _more.Set();
        }

        public void Enqueue(byte[] frame)
        {
            if (_queue.Count > 32)
            {
                byte[] drop;
                _queue.TryDequeue(out drop);
            }
            _queue.Enqueue(frame);
            _more.Set();
        }

        private void Loop()
        {
            try
            {
                while (!_stop)
                {
                    if (_resetRequested)
                    {
                        _resetRequested = false;
                        Reinit();
                    }
                    byte[] f;
                    if (!_queue.TryDequeue(out f))
                    {
                        _more.Reset();
                        if (_queue.Count == 0)
                        {
                            if (_stop) break;
                            _more.Wait(50);
                        }
                        continue;
                    }
                    FeedOne(f);
                }
            }
            catch (Exception ex)
            {
                if (!_stop) EmitStatus("decode thread: " + ex);
            }
        }

        // ---------- MFT lifecycle ----------

        private void Reinit()
        {
            DisposeMft();
            _ts = 0;
            byte[] d;
            while (_queue.TryDequeue(out d)) { }
            EmitStatus("decoder ready");
        }

        private void DisposeMft()
        {
            if (_mft != null)
            {
                try
                {
                    _mft.ProcessMessage(MfConst.MFT_MESSAGE_COMMAND_FLUSH, 0);
                }
                catch { }
                Marshal.FinalReleaseComObject(_mft);
                _mft = null;
            }
        }

        private void EnsureMft()
        {
            if (_mft != null) return;
            _mft = MfNative.CreateH264Decoder();

            Guid majType = MfGuids.MF_MT_MAJOR_TYPE;
            Guid subType = MfGuids.MF_MT_SUBTYPE;
            Guid interl = MfGuids.MF_MT_INTERLACE_MODE;
            Guid frameSize = MfGuids.MF_MT_FRAME_SIZE;
            Guid video = MfGuids.MFMediaType_Video;
            Guid h264 = MfGuids.MFVideoFormat_H264;
            Guid nv12 = MfGuids.MFVideoFormat_NV12;

            IMFMediaType inType;
            int rc = MfNative.MFCreateMediaType(out inType);
            if (rc != MfConst.S_OK) throw new System.ComponentModel.Win32Exception(rc, "MFCreateMediaType in");
            inType.SetGUID(ref majType, ref video);
            rc = inType.SetGUID(ref subType, ref h264);
            inType.SetUINT32(ref interl, MfConst.MFVideoInterlace_MixedInterlaceOrProgressive);
            inType.SetUINT64(ref frameSize, DeviceGeometry.FrameSize);
            rc = _mft.SetInputType(0, inType, 0);
            Marshal.FinalReleaseComObject(inType);
            if (rc != MfConst.S_OK)
                throw new System.ComponentModel.Win32Exception(rc, "SetInputType(H264) 0x" + rc.ToString("X8"));

            IMFMediaType outType = null;
            int rcOut = MfConst.MF_E_TRANSFORM_TYPE_NOT_SET;
            for (uint i = 0; i < 8; i++)
            {
                IMFMediaType cand;
                int arc = _mft.GetOutputAvailableType(0, i, out cand);
                if (arc != MfConst.S_OK) break;
                Guid st;
                int src = cand.GetGUID(ref subType, out st);
                if (src == MfConst.S_OK && st == nv12)
                {
                    outType = cand;
                    rcOut = MfConst.S_OK;
                    break;
                }
                Marshal.FinalReleaseComObject(cand);
            }
            if (outType == null && rcOut != MfConst.S_OK)
            {
                // fall back to the first offered type
                rcOut = _mft.GetOutputAvailableType(0, 0, out outType);
            }
            if (rcOut != MfConst.S_OK || outType == null)
                throw new System.ComponentModel.Win32Exception(rcOut, "GetOutputAvailableType 0x" + rcOut.ToString("X8"));
            rc = _mft.SetOutputType(0, outType, 0);
            Marshal.FinalReleaseComObject(outType);
            if (rc != MfConst.S_OK)
                throw new System.ComponentModel.Win32Exception(rc, "SetOutputType 0x" + rc.ToString("X8"));

            _mft.ProcessMessage(MfConst.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            _mft.ProcessMessage(MfConst.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

            // GetOutputStreamInfo on this decoder reports flags=0: it cannot
            // allocate output samples itself. The caller MUST pre-allocate one
            // and pass it in MFT_OUTPUT_DATA_BUFFER.pSample, or ProcessOutput
            // fails with E_INVALIDARG the moment a frame is ready. PumpOutput
            // creates a fresh sample per ProcessOutput call.
        }

        // ---------- feeding ----------

        private void FeedOne(byte[] f)
        {
            EnsureMft();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int rc = FeedSample(f);
                if (rc == MfConst.MF_E_NOTACCEPTING || rc == MfConst.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    PumpOutput();
                    continue;
                }
                if (rc != MfConst.S_OK)
                {
                    EmitStatus("ProcessInput 0x" + rc.ToString("X8") + " - rebuilding decoder");
                    DisposeMft();
                    return;
                }
                break;
            }
            PumpOutput();
        }

        private int FeedSample(byte[] f)
        {
            IMFSample sample;
            int rc = MfNative.MFCreateSample(out sample);
            if (rc != MfConst.S_OK) return rc;
            try
            {
                IMFMediaBuffer buf;
                rc = MfNative.MFCreateMemoryBuffer((uint)f.Length, out buf);
                if (rc != MfConst.S_OK) return rc;
                IntPtr pData;
                int maxLen, curLen;
                rc = buf.Lock(out pData, out maxLen, out curLen);
                if (rc == MfConst.S_OK)
                {
                    Marshal.Copy(f, 0, pData, f.Length);
                    buf.SetCurrentLength(f.Length);
                    buf.Unlock();
                }
                sample.AddBuffer(buf);
                Marshal.FinalReleaseComObject(buf);
                sample.SetSampleTime(_ts);
                sample.SetSampleDuration(333333); // 100ns units at 30 fps
                _ts += 333333;
                // mark keyframe access units (SPS-led or IDR) as clean points -
                // msmpeg2vdec uses this to reset its reference/ordering state
                if (f.Length >= 5)
                {
                    int nalType = f[4] & 0x1F;
                    if (nalType == 5 || nalType == 7)
                    {
                        Guid cleanPoint = MfGuids.MFSampleExtension_CleanPoint;
                        sample.SetUINT32(ref cleanPoint, 1);
                    }
                }
                rc = _mft.ProcessInput(0, sample, 0);
            }
            finally
            {
                Marshal.FinalReleaseComObject(sample);
            }
            return rc;
        }

        // ---------- output ----------

        private static readonly IntPtr _offSample = Marshal.OffsetOf(typeof(MFT_OUTPUT_DATA_BUFFER), "pSample");
        private static readonly IntPtr _offEvents = Marshal.OffsetOf(typeof(MFT_OUTPUT_DATA_BUFFER), "pEvents");
        private static readonly int _bufSize = Marshal.SizeOf(typeof(MFT_OUTPUT_DATA_BUFFER));

        private void PumpOutput()
        {
            while (true)
            {
                IntPtr ob = Marshal.AllocHGlobal(_bufSize);
                try
                {
                    for (int k = 0; k < _bufSize; k++) Marshal.WriteByte(ob, k, 0);
                    // fresh output sample per call: msmpeg2vdec reusing the same
                    // caller sample across ProcessOutput calls can keep returning
                    // the first decoded frame instead of new pictures
                    IMFSample os;
                    int orc = MfNative.MFCreateSample(out os);
                    if (orc != MfConst.S_OK) return;
                    IMFMediaBuffer obuf;
                    orc = MfNative.MFCreateMemoryBuffer(1 << 20, out obuf);
                    if (orc != MfConst.S_OK) { Marshal.FinalReleaseComObject(os); return; }
                    os.AddBuffer(obuf);
                    Marshal.FinalReleaseComObject(obuf);
                    IntPtr osPtr = Marshal.GetIUnknownForObject(os);

                    Marshal.WriteIntPtr(ob, _offSample.ToInt32(), osPtr);
                    uint status;
                    int rc = _mft.ProcessOutput(0, 1, ob, out status);
                    if (rc == MfConst.S_OK && _outStarted == 0)
                    {
                        Interlocked.Exchange(ref _outStarted, 1);
                        EmitStatus("output flow started");
                    }
                    IntPtr ps = Marshal.ReadIntPtr(ob, _offSample.ToInt32());
                    if (rc == MfConst.S_OK && ps != IntPtr.Zero)
                    {
                        IMFSample samp = (IMFSample)Marshal.GetObjectForIUnknown(ps);
                        ProcessSample(samp);
                        Marshal.FinalReleaseComObject(samp);
                        if (ps != osPtr)
                        {
                            // the MFT swapped in its own sample - it owns it
                            Marshal.Release(ps);
                        }
                    }
                    Marshal.Release(osPtr);
                    Marshal.FinalReleaseComObject(os);
                    IntPtr pe = Marshal.ReadIntPtr(ob, _offEvents.ToInt32());
                    if (pe != IntPtr.Zero) Marshal.Release(pe);
                    if (rc == MfConst.S_OK) continue;
                    if (rc == MfConst.MF_E_TRANSFORM_NEED_MORE_INPUT)
                    {
                        return;
                    }
                    if (rc == MfConst.MF_E_TRANSFORM_STREAM_CHANGE)
                    {
                        HandleStreamChange();
                        continue;
                    }
                    if (rc != MfConst.MF_E_TRANSFORM_TYPE_NOT_SET)
                    {
                        EmitStatus("ProcessOutput 0x" + rc.ToString("X8"));
                    }
                    else
                    {
                        EmitStatus("ProcessOutput: output type not set");
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(ob);
                }
            }
        }

        private void HandleStreamChange()
        {
            IMFMediaType t;
            int rc = _mft.GetOutputCurrentType(0, out t);
            if (rc != MfConst.S_OK) return;
            try
            {
                Guid fsKey = MfGuids.MF_MT_FRAME_SIZE;
                ulong size;
                int grc = t.GetUINT64(ref fsKey, out size);
                if (grc == MfConst.S_OK)
                {
                    int w = (int)(size & 0xFFFFFFFFu);
                    int h = (int)((size >> 32) & 0xFFFFFFFFu);
                    if (w > 0 && h > 0) PrepareSurface(w, h);
                }
                _mft.SetOutputType(0, t, 0);
            }
            finally
            {
                Marshal.FinalReleaseComObject(t);
            }
        }

        private void PrepareSurface(int w, int h)
        {
            int stride = (w + 15) & ~15;
            if (_w != w || _h != h || _stride != stride)
            {
                lock (_publishLock)
                {
                    _w = w;
                    _h = h;
                    _stride = stride;
                    _yPlane = new byte[stride * h];
                    _uvPlane = new byte[stride * h / 2];
                    _bgra = new int[w * h];
                    _published = null;
                    for (int i = 0; i < 2; i++)
                    {
                        if (_bmp[i] != null) { _bmp[i].Dispose(); _bmp[i] = null; }
                        _bmp[i] = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    }
                    _cur = 0;
                    EmitStatus("video " + w + "x" + h);
                }
            }
        }

        private void ProcessSample(IMFSample samp)
        {
            IMFMediaBuffer buf;
            int rc = samp.ConvertToContiguousBuffer(out buf);
            if (rc != MfConst.S_OK) return;
            try
            {
                IntPtr pData;
                int maxLen, curLen;
                rc = buf.Lock(out pData, out maxLen, out curLen);
                if (rc != MfConst.S_OK) return;
                try
                {
                    if (_w == 0 || _h == 0)
                    {
                        // first frame: assume the device geometry
                        PrepareSurface(640, 480);
                    }
                    int ySize = _stride * _h;
                    int uvSize = _stride * _h / 2;
                    if (curLen < ySize + uvSize) return;
                    Marshal.Copy(pData, _yPlane, 0, ySize);
                    Marshal.Copy(IntPtr.Add(pData, ySize), _uvPlane, 0, uvSize);
                }
                finally
                {
                    buf.Unlock();
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(buf);
            }

            Nv12ToBgra();
            lock (_publishLock)
            {
                Bitmap target = _bmp[_cur];
                if (target != null)
                {
                    BitmapData bd = target.LockBits(new Rectangle(0, 0, _w, _h),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < _h; y++)
                            Marshal.Copy(_bgra, y * _w, IntPtr.Add(bd.Scan0, y * bd.Stride), _w);
                    }
                    finally
                    {
                        target.UnlockBits(bd);
                    }
                    _published = target;
                    _cur ^= 1;
                }
            }
            Interlocked.Increment(ref _framesDecoded);
            Action<int, int> h = FrameReady;
            if (h != null) h(_w, _h);
        }

        private void EmitStatus(string msg)
        {
            Action<string> h = StatusChanged;
            if (h != null) h(msg);
        }

        // ---------- NV12 -> BGRA32 (BT.601 limited range, table-based) ----------

        private static readonly int[] _yc = BuildYTable();
        private static readonly int[] _ub = BuildTable(516);
        private static readonly int[] _ug = BuildTable(100);
        private static readonly int[] _vr = BuildTable(409);
        private static readonly int[] _vg = BuildTable(208);

        private static int[] BuildYTable()
        {
            int[] t = new int[256];
            for (int i = 0; i < 256; i++) t[i] = (298 * (i - 16) + 128) >> 8;
            return t;
        }

        private static int[] BuildTable(int weight)
        {
            int[] t = new int[256];
            for (int i = 0; i < 256; i++) t[i] = (weight * (i - 128)) >> 8;
            return t;
        }

        private unsafe void Nv12ToBgra()
        {
            int w = _w, h = _h, stride = _stride;
            fixed (byte* py0 = _yPlane)
            fixed (byte* puv0 = _uvPlane)
            fixed (int* pd0 = _bgra)
            {
                byte* py = py0;
                byte* puv = puv0;
                int* pd = pd0;
                for (int y = 0; y < h; y++)
                {
                    byte* yRow = py + y * stride;
                    byte* uvRow = puv + (y >> 1) * stride;
                    int rowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int yv = _yc[yRow[x]];
                        int uvx = (x >> 1) << 1;
                        int u = uvRow[uvx];
                        int v = uvRow[uvx + 1];
                        int r = yv + _vr[v];
                        int g = yv - _ug[u] - _vg[v];
                        int b = yv + _ub[u];
                        if (r < 0) r = 0; else if (r > 255) r = 255;
                        if (g < 0) g = 0; else if (g > 255) g = 255;
                        if (b < 0) b = 0; else if (b > 255) b = 255;
                        pd[rowBase + x] = unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
                    }
                }
            }
        }
    }
}
