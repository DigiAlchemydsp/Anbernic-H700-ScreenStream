using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FatmaVision
{
    /// <summary>
    /// Media Foundation interop for the Windows built-in H.264 video decoder MFT.
    /// All IIDs/CLSIDs verified against the Windows 10 SDK headers (mfobjects.h,
    /// mftransform.h) and registry (HKLM\SOFTWARE\Classes\CLSID).
    ///
    /// Interop rules learned on the bench (do not "clean up"):
    ///  1. Every method carries [PreserveSig] - error HRESULTs are returned as
    ///     ints so the MFT control flow can see MF_E_* codes instead of throwing.
    ///  2. IMFMediaType / IMFSample are declared FLAT (all 30 IMFAttributes slots
    ///     duplicated), NOT as derived interfaces: the .NET Framework interop
    ///     layer mis-slots the own methods of derived ComImport interfaces
    ///     (AV / wrong HRESULTs - verified empirically).
    ///  3. IID_IMFTransform = BF94C121-5B05-4E6F-8000-BA598961414D (older docs
    ///     show ...AC00-3D2F24A6DD57 which is WRONG on this machine).
    /// </summary>
    internal static class MfGuids
    {
        // Attributes
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724ce0-8b95-4a65-9ac5-166f51f39cd8");

        // Sample attributes (mfapi.h)
        public static readonly Guid MFSampleExtension_CleanPoint = new Guid("9c85c9a2-bc66-47da-bdc5-7604c18c1e49");
        public static readonly Guid MFSampleExtension_Interlaced = new Guid("86cbc910-e533-4755-8dc3-319b6b3a9555");

        // Major/subtypes
        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");

        // Interfaces (SDK-verified)
        public static readonly Guid IID_IMFAttributes = new Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3");
        public static readonly Guid IID_IMFMediaBuffer = new Guid("045FA593-8799-42B8-BC8D-8968C6453507");
        public static readonly Guid IID_IMFSample = new Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4");
        public static readonly Guid IID_IMFMediaType = new Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555");
        public static readonly Guid IID_IMFTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");

        // Microsoft H264 Video Decoder MFT (msmpeg2vdec.dll)
        public static readonly Guid CLSID_MSH264DecoderMFT = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");

        public static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
    }

    internal static class MfConst
    {
        public const uint MF_VERSION = 0x00020070;
        public const uint MFSTARTUP_FULL = 0;
        public const uint CLSCTX_INPROC_SERVER = 0x1;

        public const int S_OK = 0;

        public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        public const int MF_E_INVALID_STREAM_DATA = unchecked((int)0xC00D36CB);
        public const int MF_E_UNEXPECTED = unchecked((int)0xC00D36F2);

        public const int MFT_MESSAGE_COMMAND_FLUSH = 0;
        public const int MFT_MESSAGE_COMMAND_DRAIN = 1;
        public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = unchecked((int)0x10000000);
        public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = unchecked((int)0x10000003);

        public const int MFVideoInterlace_Progressive = 2;
        public const int MFVideoInterlace_MixedInterlaceOrProgressive = 5;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;
        public uint dwStatus;
        public IntPtr pEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public uint dwFlags;
        public uint cbSize;
        public uint cbMaxLookahead;
        public uint cbAlignment;
    }

    /// <summary>IMFAttributes - full 30-method vtable, SDK-verified.</summary>
    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }

    /// <summary>IMFMediaBuffer (5 methods).</summary>
    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(int cbCurrentLength);
        [PreserveSig] int GetMaxLength(out int pcbMaxLength);
    }

    /// <summary>
    /// IMFMediaType - FLAT vtable (30 IMFAttributes slots + 5 own).
    /// Declared flat on purpose - see class-level interop notes.
    /// </summary>
    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat(out int pfCompressed);
        [PreserveSig] int IsEqual(IMFAttributes pOther, out int pfEqual);
        [PreserveSig] int GetRepresentation(uint guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(uint guidRepresentation, IntPtr pvRepresentation);
    }

    /// <summary>
    /// IMFSample - FLAT vtable (30 IMFAttributes slots + 14 own).
    /// Declared flat on purpose - see class-level interop notes.
    /// </summary>
    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    /// <summary>IMFTransform - full 24-method vtable, SDK-verified (mftransform.h).</summary>
    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
        [PreserveSig] int GetStreamIDs(uint dwInputIDArraySize, [MarshalAs(UnmanagedType.LPArray)] uint[] pdwInputIDs, uint dwOutputIDArraySize, [MarshalAs(UnmanagedType.LPArray)] uint[] pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(uint dwStreamIndex, out MFT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetOutputStreamInfo(uint dwStreamIndex, out MFT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
        [PreserveSig] int GetInputStreamAttributes(uint dwInputStreamID, uint dwAttributes, out IMFAttributes pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(uint dwOutputStreamID, uint dwAttributes, out IMFAttributes pAttributes);
        [PreserveSig] int DeleteInputStream(uint dwStreamID);
        [PreserveSig] int AddInputStreams(uint cStreams, [MarshalAs(UnmanagedType.LPArray)] uint[] pdwStreamIDs);
        [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType pType, uint dwFlags);
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, uint dwFlags);
        [PreserveSig] int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
        [PreserveSig] int GetOutputStatus(out uint pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(int eMessage, ulong ulParam);
        [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount, IntPtr pOutputSamples, out uint pdwStatus);
    }

    /// <summary>P/Invoke entry points from mfplat.dll / ole32.dll.</summary>
    internal static class MfNative
    {
        [DllImport("mfplat.dll")]
        public static extern int MFStartup(uint Version, uint dwFlags);

        [DllImport("mfplat.dll")]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll")]
        public static extern int MFCreateSample(out IMFSample ppIMFSample);

        [DllImport("mfplat.dll")]
        public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("mfplat.dll")]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        public static IMFTransform CreateH264Decoder()
        {
            Guid clsid = MfGuids.CLSID_MSH264DecoderMFT;
            Guid iid = MfGuids.IID_IMFTransform;
            IntPtr p;
            int rc = CoCreateInstance(ref clsid, IntPtr.Zero, MfConst.CLSCTX_INPROC_SERVER,
                ref iid, out p);
            if (rc != MfConst.S_OK)
                throw new System.ComponentModel.Win32Exception(
                    rc, "CoCreateInstance(MS H264 decoder MFT) failed 0x" + rc.ToString("X8") +
                    " - H.264 decode support missing? (Windows N edition / no Media Feature Pack)");
            return (IMFTransform)Marshal.GetObjectForIUnknown(p);
        }
    }
}