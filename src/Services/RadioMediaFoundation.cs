using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ElansAddonHub.Services
{
    // --selftest "radio" (2.76): AAC with what Windows already has. Windows 10 / 11 ship Media Foundation's AAC decoder (msmpeg2adec.dll, also
    // HE-AAC v1 / v2, which Serenity's 64k stream is); the player feeds it the stream's ADTS frames one by one and takes 16-bit PCM back. No
    // package, no download. Windows "N" editions without the Media Feature Pack have no Media Foundation: Available() is false there and the
    // player falls back (RadioParadise.Resolve). The encoder half (MfAacEncoder) is only for the self-test, which makes its own test stream.
    public static class RadioMf
    {
        public static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        public static readonly Guid MF_MT_USER_DATA = new Guid("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
        public static readonly Guid CLSID_AacDecoder = new Guid("32d186a7-218f-4c75-8876-dd77273a8999");
        public static readonly Guid CLSID_AacEncoder = new Guid("93AF0C51-2275-45d2-A35B-F2BA21CAED00");

        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        const int MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1;
        public const int MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
        public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer buffer);

        public static void Startup() { Check(MFStartup(MF_VERSION, MFSTARTUP_LITE)); }
        public static void Shutdown() { try { MFShutdown(); } catch { } }
        public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        public static IMFTransform Create(Guid clsid)
        {
            var t = Type.GetTypeFromCLSID(clsid, true);
            return (IMFTransform)Activator.CreateInstance(t);
        }

        public static IMFMediaType NewType()
        {
            Check(MFCreateMediaType(out var t));
            return t;
        }

        public static IMFMediaType AudioType(Guid subtype, int rate, int channels, int bits)
        {
            var t = NewType();
            var g1 = MF_MT_MAJOR_TYPE; var v1 = MFMediaType_Audio; t.SetGUID(ref g1, ref v1);
            var g2 = MF_MT_SUBTYPE; var v2 = subtype; t.SetGUID(ref g2, ref v2);
            Set(t, MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            Set(t, MF_MT_AUDIO_NUM_CHANNELS, channels);
            Set(t, MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
            return t;
        }

        public static void Set(IMFMediaType t, Guid key, int v) { Check(t.SetUINT32(ref key, v)); }
        public static int Get(IMFMediaType t, Guid key) { return t.GetUINT32(ref key, out var v) < 0 ? 0 : v; }
        public static Guid GetGuid(IMFMediaType t, Guid key) { return t.GetGUID(ref key, out var v) < 0 ? Guid.Empty : v; }

        public static IMFSample Sample(byte[] data, int off, int len, int capacity = 0)
        {
            Check(MFCreateSample(out var s));
            Check(MFCreateMemoryBuffer(Math.Max(Math.Max(len, capacity), 1), out var b));
            if (len > 0)
            {
                Check(b.Lock(out var p, out _, out _));
                Marshal.Copy(data, off, p, len);
                b.Unlock();
            }
            b.SetCurrentLength(len);
            s.AddBuffer(b);
            Marshal.ReleaseComObject(b);
            return s;
        }

        // the bytes of a sample (appended to out)
        public static int CopyOut(IMFSample s, ref byte[] dst, int at)
        {
            s.ConvertToContiguousBuffer(out var b);
            try
            {
                Check(b.Lock(out var p, out _, out var cur));
                try
                {
                    if (dst == null || dst.Length < at + cur) Array.Resize(ref dst, Math.Max(at + cur, (dst?.Length ?? 0) * 2));
                    Marshal.Copy(p, dst, at, cur);
                }
                finally { b.Unlock(); }
                return cur;
            }
            finally { Marshal.ReleaseComObject(b); }
        }

        // ---- the decoder can be made and accepts an AAC LC 44.1 kHz stereo stream (asked once; the self-test can pretend it is missing)
        static bool? available;
        static readonly object probeGate = new object();
        public static bool Available()
        {
            lock (probeGate)
            {
                if (available == null)
                    try { using (var d = new MfAacDecoder(1, 4, 2)) available = d.OutputFormat != null; }   // AAC LC (ADTS profile 1), 44.1 kHz, stereo
                    catch { available = false; }
                return available.Value;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER { public int dwStreamID; public IntPtr pSample; public int dwStatus; public IntPtr pEvents; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO { public int dwFlags; public int cbSize; public int cbAlignment; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_INPUT_STREAM_INFO { public long hnsMaxLatency; public int dwFlags; public int cbSize; public int cbMaxLookahead; public int cbAlignment; }

    // IMFAttributes' 30 methods come first in every interface below (COM interop needs them repeated); the unused ones are placeholders
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare();
        [PreserveSig] int GetUINT32([In] ref Guid key, out int value);
        void _GetUINT64(); void _GetDouble();
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        void _GetStringLength(); void _GetString(); void _GetAllocatedString();
        [PreserveSig] int GetBlobSize([In] ref Guid key, out int size);
        [PreserveSig] int GetBlob([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, int size, out int got);
        void _GetAllocatedBlob(); void _GetUnknown(); void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, int value);
        void _SetUINT64(); void _SetDouble();
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        void _SetString();
        [PreserveSig] int SetBlob([In] ref Guid key, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, int size);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int max, out int current);
        void Unlock();
        void GetCurrentLength(out int len);
        void SetCurrentLength(int len);
        void GetMaxLength(out int len);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare(); void _GetUINT32(); void _GetUINT64(); void _GetDouble(); void _GetGUID();
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems(); void _SetUINT32(); void _SetUINT64(); void _SetDouble(); void _SetGUID(); void _SetString();
        void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore(); void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        void GetSampleFlags(out int flags);
        void SetSampleFlags(int flags);
        void GetSampleTime(out long t);
        void SetSampleTime(long t);
        void GetSampleDuration(out long d);
        void SetSampleDuration(long d);
        void GetBufferCount(out int n);
        void GetBufferByIndex(int i, out IMFMediaBuffer b);
        void ConvertToContiguousBuffer(out IMFMediaBuffer b);
        void AddBuffer(IMFMediaBuffer b);
        void RemoveBufferByIndex(int i);
        void RemoveAllBuffers();
        void GetTotalLength(out int len);
    }

    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFTransform
    {
        void GetStreamLimits(out int inMin, out int inMax, out int outMin, out int outMax);
        void GetStreamCount(out int inputs, out int outputs);
        [PreserveSig] int GetStreamIDs(int inSize, [Out, MarshalAs(UnmanagedType.LPArray)] int[] inIds, int outSize, [Out, MarshalAs(UnmanagedType.LPArray)] int[] outIds);
        void GetInputStreamInfo(int id, out MFT_INPUT_STREAM_INFO info);
        void GetOutputStreamInfo(int id, out MFT_OUTPUT_STREAM_INFO info);
        [PreserveSig] int GetAttributes(out IntPtr attrs);
        [PreserveSig] int GetInputStreamAttributes(int id, out IntPtr attrs);
        [PreserveSig] int GetOutputStreamAttributes(int id, out IntPtr attrs);
        [PreserveSig] int DeleteInputStream(int id);
        [PreserveSig] int AddInputStreams(int n, [In, MarshalAs(UnmanagedType.LPArray)] int[] ids);
        [PreserveSig] int GetInputAvailableType(int id, int index, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(int id, int index, out IMFMediaType type);
        [PreserveSig] int SetInputType(int id, IMFMediaType type, int flags);
        [PreserveSig] int SetOutputType(int id, IMFMediaType type, int flags);
        [PreserveSig] int GetInputCurrentType(int id, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(int id, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(int id, out int flags);
        [PreserveSig] int GetOutputStatus(out int flags);
        [PreserveSig] int SetOutputBounds(long lower, long upper);
        [PreserveSig] int ProcessEvent(int id, IntPtr evt);
        [PreserveSig] int ProcessMessage(int msg, IntPtr param);
        [PreserveSig] int ProcessInput(int id, IMFSample sample, int flags);
        [PreserveSig] int ProcessOutput(int flags, int count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] MFT_OUTPUT_DATA_BUFFER[] buffers, out int status);
    }

    // a Media Foundation transform driven by hand: bytes in, bytes out (one input and one output stream)
    public abstract class MfTransformBase : IDisposable
    {
        protected IMFTransform mft;
        MFT_OUTPUT_STREAM_INFO outInfo;
        IMFSample outSample;
        readonly MFT_OUTPUT_DATA_BUFFER[] outBuf = new MFT_OUTPUT_DATA_BUFFER[1];
        bool started, disposed;
        protected readonly List<int> Chunks = new List<int>();   // the length of each output sample of the last Process

        protected MfTransformBase(Guid clsid)
        {
            RadioMf.Startup();
            started = true;
            try { mft = RadioMf.Create(clsid); }
            catch { Dispose(); throw; }
        }

        protected void Begin()
        {
            mft.GetOutputStreamInfo(0, out outInfo);
            mft.ProcessMessage(RadioMf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            mft.ProcessMessage(RadioMf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        }

        // feeds one piece; everything the transform gives back is appended to dst from at; returns the bytes written
        protected int Process(byte[] data, int off, int len, long time, ref byte[] dst, int at)
        {
            int written = 0;
            var input = RadioMf.Sample(data, off, len);
            try
            {
                input.SetSampleTime(time);
                for (int tries = 0; ; tries++)
                {
                    int hr = mft.ProcessInput(0, input, 0);
                    if (hr == RadioMf.MF_E_NOTACCEPTING && tries < 2) { written += Drain(ref dst, at + written); continue; }
                    RadioMf.Check(hr);
                    break;
                }
            }
            finally { Marshal.ReleaseComObject(input); }
            written += Drain(ref dst, at + written);
            return written;
        }

        int Drain(ref byte[] dst, int at)
        {
            int written = 0;
            for (int guard = 0; guard < 64; guard++)
            {
                bool own = (outInfo.dwFlags & RadioMf.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0;
                IntPtr p = IntPtr.Zero;
                if (own)
                {
                    if (outSample == null) outSample = RadioMf.Sample(null, 0, 0, Math.Max(outInfo.cbSize, 64 * 1024));
                    outSample.GetBufferByIndex(0, out var b); b.SetCurrentLength(0); Marshal.ReleaseComObject(b);
                    p = Marshal.GetComInterfaceForObject(outSample, typeof(IMFSample));
                }
                outBuf[0] = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = p };
                int hr;
                try { hr = mft.ProcessOutput(0, 1, outBuf, out _); }
                finally { if (p != IntPtr.Zero) Marshal.Release(p); }
                if (outBuf[0].pEvents != IntPtr.Zero) { Marshal.Release(outBuf[0].pEvents); outBuf[0].pEvents = IntPtr.Zero; }
                if (hr == RadioMf.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                if (hr == RadioMf.MF_E_TRANSFORM_STREAM_CHANGE) { OnStreamChange(); mft.GetOutputStreamInfo(0, out outInfo); FreeOut(); continue; }
                RadioMf.Check(hr);
                int got = 0;
                if (own) got = RadioMf.CopyOut(outSample, ref dst, at + written);
                else if (outBuf[0].pSample != IntPtr.Zero)
                {
                    var s = (IMFSample)Marshal.GetObjectForIUnknown(outBuf[0].pSample);
                    Marshal.Release(outBuf[0].pSample);
                    try { got = RadioMf.CopyOut(s, ref dst, at + written); } finally { Marshal.ReleaseComObject(s); }
                }
                if (got > 0) Chunks.Add(got);
                written += got;
            }
            return written;
        }

        protected virtual void OnStreamChange() { }

        void FreeOut() { if (outSample != null) { Marshal.ReleaseComObject(outSample); outSample = null; } }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            FreeOut();
            if (mft != null) { try { Marshal.ReleaseComObject(mft); } catch { } mft = null; }
            if (started) RadioMf.Shutdown();
        }
    }

    // AAC (one raw access unit per call, from an ADTS frame) -> 16-bit PCM
    public sealed class MfAacDecoder : MfTransformBase
    {
        public NAudio.Wave.WaveFormat OutputFormat { get; private set; }
        long time;

        // profile: the ADTS profile (1 = LC); rateIndex / channelConfig as in ADTS
        public MfAacDecoder(int profile, int rateIndex, int channelConfig) : base(RadioMf.CLSID_AacDecoder)
        {
            try
            {
                int rate = AdtsReader.Rate(rateIndex);
                var t = RadioMf.AudioType(RadioMf.MFAudioFormat_AAC, rate, Math.Max(1, channelConfig), 16);
                RadioMf.Set(t, RadioMf.MF_MT_AAC_PAYLOAD_TYPE, 0);
                // HEAACWAVEINFO after its WAVEFORMATEX (payload type 0 = raw, profile level 0xFE = not given), then the AudioSpecificConfig
                int ot = profile + 1;
                var ud = new byte[14];
                ud[0] = 0; ud[2] = 0xFE;
                ud[12] = (byte)((ot << 3) | (rateIndex >> 1));
                ud[13] = (byte)(((rateIndex & 1) << 7) | (channelConfig << 3));
                var key = RadioMf.MF_MT_USER_DATA;
                RadioMf.Check(t.SetBlob(ref key, ud, ud.Length));
                RadioMf.Check(mft.SetInputType(0, t, 0));
                Marshal.ReleaseComObject(t);
                PickOutput();
                Begin();
            }
            catch { Dispose(); throw; }
        }

        void PickOutput()
        {
            for (int i = 0; ; i++)
            {
                if (mft.GetOutputAvailableType(0, i, out var t) < 0) throw new NotSupportedException("the AAC decoder offers no PCM output");
                try
                {
                    if (RadioMf.GetGuid(t, RadioMf.MF_MT_SUBTYPE) != RadioMf.MFAudioFormat_PCM || RadioMf.Get(t, RadioMf.MF_MT_AUDIO_BITS_PER_SAMPLE) != 16) continue;
                    RadioMf.Check(mft.SetOutputType(0, t, 0));
                    OutputFormat = new NAudio.Wave.WaveFormat(RadioMf.Get(t, RadioMf.MF_MT_AUDIO_SAMPLES_PER_SECOND), 16, RadioMf.Get(t, RadioMf.MF_MT_AUDIO_NUM_CHANNELS));
                    return;
                }
                finally { Marshal.ReleaseComObject(t); }
            }
        }

        protected override void OnStreamChange() => PickOutput();

        public int Decode(byte[] au, int off, int len, ref byte[] pcm)
        {
            int n = Process(au, off, len, time, ref pcm, 0);
            time += 1024L * 10000000 / Math.Max(1, OutputFormat?.SampleRate ?? 44100);
            return n;
        }
    }

    // PCM 16-bit -> raw AAC LC access units (the self-test makes its fake stream with it; never used for playback)
    public sealed class MfAacEncoder : MfTransformBase
    {
        long time;
        readonly int rate, channels;
        public MfAacEncoder(int rate, int channels, int bytesPerSecond = 16000) : base(RadioMf.CLSID_AacEncoder)
        {
            this.rate = rate; this.channels = channels;
            try
            {
                var o = RadioMf.AudioType(RadioMf.MFAudioFormat_AAC, rate, channels, 16);
                RadioMf.Set(o, RadioMf.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytesPerSecond);
                RadioMf.Check(mft.SetOutputType(0, o, 0));
                Marshal.ReleaseComObject(o);
                var i = RadioMf.AudioType(RadioMf.MFAudioFormat_PCM, rate, channels, 16);
                RadioMf.Set(i, RadioMf.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2);
                RadioMf.Set(i, RadioMf.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * channels * 2);
                RadioMf.Check(mft.SetInputType(0, i, 0));
                Marshal.ReleaseComObject(i);
                Begin();
            }
            catch { Dispose(); throw; }
        }

        // the access units (each output sample of the encoder is one)
        public List<byte[]> Encode(short[] pcm)
        {
            var units = new List<byte[]>();
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            int step = 1024 * channels * 2;
            byte[] dst = null;
            for (int at = 0; at < bytes.Length; at += step)
            {
                int len = Math.Min(step, bytes.Length - at);
                Chunks.Clear();
                Process(bytes, at, len, time, ref dst, 0);
                int o = 0;
                foreach (var n in Chunks) { var u = new byte[n]; Buffer.BlockCopy(dst, o, u, 0, n); units.Add(u); o += n; }
                time += (long)len / (channels * 2) * 10000000 / rate;
            }
            return units;
        }
    }

    // ADTS frames (AAC's own framing in a plain stream): finds the sync, reads the header, gives back the raw access unit
    public sealed class AdtsReader
    {
        readonly Stream src;
        readonly byte[] h = new byte[9];
        readonly Stack<int> back = new Stack<int>();
        public int Profile, RateIndex, ChannelConfig, Frames, Resyncs;

        static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        public static int Rate(int index) => index >= 0 && index < Rates.Length ? Rates[index] : 44100;

        public AdtsReader(Stream src) { this.src = src; }

        int ReadByte() => back.Count > 0 ? back.Pop() : src.ReadByte();

        // the next frame's access unit (without the header) into buf; returns its length, -1 at the end of the stream
        public int Next(ref byte[] buf)
        {
            while (true)
            {
                int b0 = ReadByte(); if (b0 < 0) return -1;
                if (b0 != 0xFF) { Resyncs++; continue; }
                int b1 = ReadByte(); if (b1 < 0) return -1;
                if ((b1 & 0xF6) != 0xF0) { back.Push(b1); Resyncs++; continue; }
                h[0] = 0xFF; h[1] = (byte)b1;
                for (int i = 2; i < 7; i++) { int b = ReadByte(); if (b < 0) return -1; h[i] = (byte)b; }
                int len = ((h[3] & 3) << 11) | (h[4] << 3) | (h[5] >> 5);
                bool crc = (h[1] & 1) == 0;
                int hl = crc ? 9 : 7;
                int rate = (h[2] >> 2) & 15;
                if (len <= hl || rate >= 13)
                {
                    for (int i = 6; i >= 2; i--) back.Push(h[i]);   // a false sync: look again from the byte after it
                    Resyncs++;
                    continue;
                }
                if (crc) for (int i = 7; i < 9; i++) { int b = ReadByte(); if (b < 0) return -1; h[i] = (byte)b; }
                Profile = h[2] >> 6; RateIndex = rate; ChannelConfig = ((h[2] & 1) << 2) | (h[3] >> 6);
                int n = len - hl;
                if (buf == null || buf.Length < n) buf = new byte[Math.Max(n, 2048)];
                int off = 0;
                while (off < n && back.Count > 0) buf[off++] = (byte)back.Pop();
                while (off < n) { int r = src.Read(buf, off, n - off); if (r <= 0) return -1; off += r; }
                Frames++;
                return n;
            }
        }

        // a 7-byte ADTS header for an access unit of len bytes (self-test streams)
        public static byte[] Header(int profile, int rateIndex, int channelConfig, int len)
        {
            int fl = len + 7;
            return new[]
            {
                (byte)0xFF, (byte)0xF1,
                (byte)((profile << 6) | (rateIndex << 2) | (channelConfig >> 2)),
                (byte)(((channelConfig & 3) << 6) | (fl >> 11)),
                (byte)((fl >> 3) & 0xFF),
                (byte)(((fl & 7) << 5) | 0x1F),
                (byte)0xFC,
            };
        }
    }
}
