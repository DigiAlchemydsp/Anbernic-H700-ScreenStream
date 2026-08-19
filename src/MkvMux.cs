using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FatmaVision
{
    /// <summary>
    /// Minimal in-process Matroska muxer for the raw Annex-B H.264 access units
    /// produced by the device stream - no ffmpeg on the client. Writes a single
    /// video track (V_MPEG4/ISO/AVC) with CodecPrivate built from the in-band
    /// SPS/PPS, arrival-time timestamps and keyframe flags. Output plays in
    /// VLC/ffmpeg/media players. Files land in &lt;exe-dir&gt;\Captures.
    /// </summary>
    public class MkvRecorder : IDisposable
    {
        private const uint IdEbml = 0x1A45DFA3;
        private const uint IdSegment = 0x18538067;
        private const uint IdInfo = 0x1549A966;
        private const uint IdTimecodeScale = 0x2AD7B1;
        private const uint IdMuxingApp = 0x4D80;
        private const uint IdWritingApp = 0x5741;
        private const uint IdTracks = 0x1654AE6B;
        private const uint IdTrackEntry = 0x0AE;
        private const uint IdTrackNumber = 0x0D7;
        private const uint IdTrackUID = 0x73C5;
        private const uint IdTrackType = 0x83;
        private const uint IdCodecID = 0x86;
        private const uint IdCodecPrivate = 0x63A2;
        private const uint IdVideo = 0x0E0;
        private const uint IdPixelWidth = 0x0B0;
        private const uint IdPixelHeight = 0x0BA;
        private const uint IdCluster = 0x1F43B675;
        private const uint IdClusterTimecode = 0x0E7;
        private const uint IdSimpleBlock = 0x0A3;

        private readonly FileStream _fs;
        private readonly object _lock = new object();
        private readonly int _width;
        private readonly int _height;

        private long _segmentSizePos;
        private long _clusterSizePos = -1;
        private bool _headerWritten;
        private bool _closed;
        private int _t0 = -1;
        private long _clusterStartTime = -1;
        private byte[] _sps;
        private byte[] _pps;

        public MkvRecorder(string path, int width = 640, int height = 480)
        {
            _width = width;
            _height = height;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteEbmlHeader();
            W(IdBytes(IdSegment));
            _segmentSizePos = _fs.Position;
            WriteFixedSize8(0); // patched on Close
        }

        private void W(byte[] b)
        {
            _fs.Write(b, 0, b.Length);
        }

        private void WriteEbmlHeader()
        {
            WriteElement(IdEbml, delegate
            {
                WriteUIntElement(0x4286, 1);   // EBMLVersion
                WriteUIntElement(0x42F7, 1);   // EBMLReadVersion
                WriteUIntElement(0x42F2, 4);   // EBMLMaxIDLength
                WriteUIntElement(0x42F3, 8);   // EBMLMaxSizeLength
                WriteStringElement(0x4282, "matroska"); // DocType
                WriteUIntElement(0x4287, 2);   // DocTypeVersion
                WriteUIntElement(0x4285, 2);   // DocTypeReadVersion
            });
        }

        /// <summary>Adds one complete Annex-B access unit (00 00 00 01 / 00 00 01
        /// delimited NALs). Timestamped by arrival time. Thread-safe.</summary>
        public void AddFrame(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;
            lock (_lock)
            {
                if (_closed) return;
                if (_t0 < 0) _t0 = Environment.TickCount;
                long t = Environment.TickCount - _t0;

                if (!_headerWritten)
                {
                    EnsureParams(frame);
                    if (_sps == null) return; // need SPS before writing the track
                    WriteInfoAndTracks();
                    _headerWritten = true;
                }

                if (_clusterStartTime < 0 || t - _clusterStartTime > 30000)
                {
                    if (_clusterSizePos >= 0)
                        PatchSize8(_clusterSizePos, _fs.Position - _clusterSizePos - 8);
                    _clusterStartTime = t;
                    W(IdBytes(IdCluster));
                    _clusterSizePos = _fs.Position;
                    WriteFixedSize8(0); // patched on cluster rollover / Close
                    WriteUIntElement(IdClusterTimecode, (ulong)t);
                }

                List<byte[]> nals = SplitNals(frame);
                if (nals.Count == 0) return;
                bool key = false;
                foreach (byte[] n in nals)
                {
                    if ((n[0] & 0x1F) == 5) { key = true; break; }
                }

                WriteElementHeader(IdSimpleBlock, 1 + 2 + 1 + NalsLength(nals));
                WriteVint(_fs, 1); // track number
                W(ToBigEndian16((short)(t - _clusterStartTime)));
                _fs.WriteByte((byte)(key ? 0x80 : 0x00));
                foreach (byte[] n in nals)
                {
                    W(ToBigEndian32(n.Length));
                    _fs.Write(n, 0, n.Length);
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_closed) return;
                _closed = true;
                if (!_headerWritten)
                {
                    // empty recording: keep the file structurally valid
                    _sps = new byte[] { 0x67, 0x42, 0x00, 0x1E, 0x95, 0xA8, 0x28, 0x0F };
                    _pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
                    WriteInfoAndTracks();
                }
                long end = _fs.Position; // true end BEFORE any patch seeks
                if (_clusterSizePos >= 0)
                    PatchSize8(_clusterSizePos, end - _clusterSizePos - 8);
                _fs.Position = _segmentSizePos;
                WriteFixedSize8(end - _segmentSizePos - 8);
                _fs.Flush();
                _fs.Close();
            }
        }

        public void Dispose() { Close(); }

        // ---------- header building ----------

        private void EnsureParams(byte[] frame)
        {
            foreach (byte[] n in SplitNals(frame))
            {
                int type = n[0] & 0x1F;
                if (type == 7 && _sps == null) _sps = n;
                else if (type == 8 && _pps == null) _pps = n;
            }
        }

        private void WriteInfoAndTracks()
        {
            WriteElement(IdInfo, delegate
            {
                WriteUIntElement(IdTimecodeScale, 1000000UL); // 1 ms
                WriteStringElement(IdMuxingApp, "FatmaVision");
                WriteStringElement(IdWritingApp, "FatmaVision");
            });
            WriteElement(IdTracks, delegate
            {
                WriteElement(IdTrackEntry, delegate
                {
                    WriteUIntElement(IdTrackNumber, 1);
                    WriteUIntElement(IdTrackUID, 1);
                    WriteUIntElement(IdTrackType, 1); // video
                    WriteStringElement(IdCodecID, "V_MPEG4/ISO/AVC");
                    WriteBinaryElement(IdCodecPrivate, BuildCodecPrivate());
                    WriteElement(IdVideo, delegate
                    {
                        WriteUIntElement(IdPixelWidth, (ulong)_width);
                        WriteUIntElement(IdPixelHeight, (ulong)_height);
                    });
                });
            });
        }

        /// <summary>AVCDecoderConfigurationRecord from the in-band SPS/PPS.</summary>
        private byte[] BuildCodecPrivate()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ms.WriteByte(0x01); // configurationVersion
                ms.WriteByte(_sps[1]); // profile
                ms.WriteByte(_sps[2]); // profile_compat
                ms.WriteByte(_sps[3]); // level
                ms.WriteByte(0xFF);    // lengthSizeMinusOne = 3
                ms.WriteByte(0xE1);    // numSPS = 1
                byte[] splen = ToBigEndian16(_sps.Length);
                ms.Write(splen, 0, splen.Length);
                ms.Write(_sps, 0, _sps.Length);
                ms.WriteByte(0x01);    // numPPS = 1
                if (_pps != null)
                {
                    byte[] pplen = ToBigEndian16(_pps.Length);
                    ms.Write(pplen, 0, pplen.Length);
                    ms.Write(_pps, 0, _pps.Length);
                }
                else
                {
                    byte[] zero = ToBigEndian16(0);
                    ms.Write(zero, 0, zero.Length);
                }
                return ms.ToArray();
            }
        }

        // ---------- EBML plumbing ----------

        private static int NalsLength(List<byte[]> nals)
        {
            int len = 0;
            foreach (byte[] n in nals) len += 4 + n.Length;
            return len;
        }

        private static List<byte[]> SplitNals(byte[] frame)
        {
            List<byte[]> nals = new List<byte[]>();
            int i = 0, len = frame.Length;
            while (i < len)
            {
                int sc = 3;
                if (i + 4 <= len && frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 0 && frame[i + 3] == 1) { sc = 4; }
                else if (i + 3 <= len && frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1) { sc = 3; }
                else { i++; continue; }
                int start = i + sc;
                int j = start;
                while (j < len)
                {
                    if (j + 4 <= len && frame[j] == 0 && frame[j + 1] == 0 && frame[j + 2] == 0 && frame[j + 3] == 1) break;
                    if (j + 3 <= len && frame[j] == 0 && frame[j + 1] == 0 && frame[j + 2] == 1) break;
                    j++;
                }
                if (j > start)
                {
                    byte[] nal = new byte[j - start];
                    Buffer.BlockCopy(frame, start, nal, 0, nal.Length);
                    if ((nal[0] & 0x1F) != 9) nals.Add(nal); // drop AUD
                }
                i = j;
            }
            return nals;
        }

        private void WriteElement(uint id, Action payload)
        {
            W(IdBytes(id));
            long sizePos = _fs.Position;
            WriteFixedSize8(0); // 8-byte size vint placeholder, patched below
            long dataStart = _fs.Position;
            payload();
            long dataEnd = _fs.Position;
            PatchSize8(sizePos, dataEnd - dataStart);
            _fs.Position = dataEnd;
        }

        private void WriteElementHeader(uint id, long payloadLen)
        {
            W(IdBytes(id));
            WriteVintSize(_fs, payloadLen);
        }

        private void PatchSize8(long sizePos, long size)
        {
            long save = _fs.Position;
            _fs.Position = sizePos + 1; // skip the 0x01 length-marker byte
            for (int i = 6; i >= 0; i--) _fs.WriteByte((byte)(size >> (8 * i)));
            _fs.Position = save; // MUST restore - callers keep writing at the end
        }

        private void WriteUIntElement(uint id, ulong value)
        {
            byte[] v = ToBigEndianMinimal(value);
            WriteElementHeader(id, v.Length);
            _fs.Write(v, 0, v.Length);
        }

        private void WriteStringElement(uint id, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            WriteElementHeader(id, b.Length);
            _fs.Write(b, 0, b.Length);
        }

        private void WriteBinaryElement(uint id, byte[] data)
        {
            WriteElementHeader(id, data.Length);
            _fs.Write(data, 0, data.Length);
        }

        private static byte[] IdBytes(uint id)
        {
            if (id <= 0xFF) return new byte[] { (byte)id };
            if (id <= 0xFFFF) return new byte[] { (byte)(id >> 8), (byte)id };
            if (id <= 0xFFFFFF) return new byte[] { (byte)(id >> 16), (byte)(id >> 8), (byte)id };
            return new byte[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };
        }

        private static void WriteVintSize(Stream s, long size)
        {
            // all-value-bits-set encodings (0xFF, 0x7FFF, 0x3FFFFF, ...) are the
            // EBML "unknown size" sentinels - reserve one value per length
            int len = 1;
            if (size >= 127) len = 2;                       // 0xFF reserved
            if (size >= 16383) len = 3;                     // 0x7FFF reserved
            if (size >= 2097151) len = 4;
            if (size >= 268435455) len = 5;
            if (size >= 34359738367) len = 6;
            if (size >= 4398046511103) len = 7;
            if (size >= 562949953421311) len = 8;
            s.WriteByte((byte)((1 << (8 - len)) | (byte)(size >> (8 * (len - 1)))));
            for (int i = len - 2; i >= 0; i--) s.WriteByte((byte)(size >> (8 * i)));
        }

        private static void WriteVint(Stream s, long value)
        {
            WriteVintSize(s, value);
        }

        private void WriteFixedSize8(long size)
        {
            _fs.WriteByte(0x01); // 8-byte size vint
            for (int i = 6; i >= 0; i--) _fs.WriteByte((byte)(size >> (8 * i)));
        }

        private static byte[] ToBigEndianMinimal(ulong v)
        {
            int len = 1;
            ulong t = v;
            while (t > 255) { t >>= 8; len++; }
            byte[] b = new byte[len];
            for (int i = len - 1; i >= 0; i--) { b[i] = (byte)v; v >>= 8; }
            return b;
        }

        private static byte[] ToBigEndian16(short v)
        {
            return new byte[] { (byte)(v >> 8), (byte)v };
        }

        private static byte[] ToBigEndian16(int v)
        {
            return new byte[] { (byte)(v >> 8), (byte)v };
        }

        private static byte[] ToBigEndian32(int v)
        {
            return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }
    }
}
