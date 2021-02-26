using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace GitHub.secile.Audio
{
    class AudioInput
    {
        // [How to use]
        // string[] devices = AudioInput.FindDevices();
        // if (devices.Length == 0) return; // no device.

        // create AudioInput with default device.
        // var device = new AudioInput(44100, 16, 2);

        // start sampling
        // device.Start(data =>
        // {
        //     // called when each buffer becomes full
        //     Console.WriteLine(data.Length);
        // });

        // this.FormClosing += (s, ev) =>
        // {
        //     device.Stop();
        //     device.Close();
        // };

        private IntPtr Device;

        private const uint WAVE_MAPPER = 0xFFFFFFFF;

        /// <summary>data bytes required per second.</summary>
        public readonly int BytesPerSec;

        /// <summary>create AudioInput with default device.</summary>
        public AudioInput(int samplesPerSec, int bitsPerSamples, int channels) : this(samplesPerSec, bitsPerSamples, channels, WAVE_MAPPER) { }

        /// <summary>create AudioInput with specified device.</summary>
        /// <param name="deviceId">index of FindDevices result.</param>
        public AudioInput(int samplesPerSec, int bitsPerSamples, int channels, uint deviceId)
        {
            var format = new Win32.WaveFormatEx(samplesPerSec, bitsPerSamples, channels);
            BytesPerSec = samplesPerSec * channels * (bitsPerSamples / 8);

            WaveInProc = WaveInProcAndThreadStart();

            int rc = Win32.waveInOpen(out Device, deviceId, ref format, WaveInProc, IntPtr.Zero, Win32.CALLBACK_FUNCTION);
            if (rc != Win32.MMSYSERR_NOERROR)
            {
                var sb = new StringBuilder(256);
                Win32.waveInGetErrorText(rc, sb, sb.Capacity);
                throw new InvalidOperationException(sb.ToString());
            }
        }

        private Win32.WaveInProc WaveInProc;
        private Win32.WaveInProc WaveInProcAndThreadStart()
        {
            var proc_to_thread_event = new System.Threading.AutoResetEvent(false);
            var proc_to_thread_param = new Queue<IntPtr>();

            // callback
            var proc = new Win32.WaveInProc((hwo, msg, user, param1, param2) =>
            {
                // [waveInProc callback function remarks]
                // Applications should not call any system-defined functions from inside a callback function, except for
                // EnterCriticalSection, LeaveCriticalSection, midiOutLongMsg, midiOutShortMsg, OutputDebugString,
                // PostMessage, PostThreadMessage, SetEvent, timeGetSystemTime, timeGetTime, timeKillEvent, and timeSetEvent.
                // Calling other wave functions will cause deadlock.

                // Data:the device driver is finished with a data block
                if (msg == Win32.WaveInMessage.Data)
                {
                    lock (proc_to_thread_param)
                    {
                        proc_to_thread_param.Enqueue(param1);
                    }

                    proc_to_thread_event.Set();
                }
            });

            // thread
            var thread = new System.Threading.Thread(() =>
            {
                IntPtr header_ptr;

                while (true)
                {
                    proc_to_thread_event.WaitOne(); // wait until event is rased.
                    {
                        lock (proc_to_thread_param)
                        {
                            header_ptr = proc_to_thread_param.Dequeue();
                        }

                        var header = Win32.WaveHeader.FromIntPtr(header_ptr);
                        int bytesRecorded = (int)header.dwBytesRecorded;

                        // bytesRecored can be 0 if reset invoked.
                        if (bytesRecorded > 0)
                        {
                            var buffer = new byte[bytesRecorded];
                            Marshal.Copy(header.lpData, buffer, 0, bytesRecorded);
                            OnRecieved(buffer);
                        }

                        if (Active)
                        {
                            // keep sampling. recycle buffer.
                            Win32.waveInAddBuffer(Device, header_ptr, Win32.WaveHeader.Size);
                        }
                        else
                        {
                            // stop sampling. release buffer.
                            Win32.waveInUnprepareHeader(Device, header_ptr, Win32.WaveHeader.Size);
                            var data_handle = GCHandle.FromIntPtr(header.dwUser);
                            data_handle.Free();
                            Marshal.FreeHGlobal(header_ptr);
                        }
                    }
                }
            });
            thread.Name = "WaveInThread";
            thread.IsBackground = true;
            thread.Start();

            return proc;
        }

        private bool Active = false;

        private Action<byte[]> OnRecieved;

        /// <summary>start sampling.</summary>
        /// <param name="onRecieved">
        /// called when each buffer becomes full, by default every second.
        /// if bitsPerSamples == 8, 1 byte represent sigle byte value from 0 to 255, base line = 128.
        /// if bitsPerSamples == 16, 2 bytes represent single short value(little endian) from -32767 to 32767, base line = 0.
        /// if stereo (Channels = 2), data order is LRLR...
        /// </param>
        public void Start(Action<byte[]> onRecieved)
        {
            Start(onRecieved, BytesPerSec);
        }

        /// <summary>start sampling.</summary>
        /// <param name="onRecieved">
        /// called when each buffer becomes full.
        /// if bitsPerSamples == 8, 1 byte represent sigle byte value from 0 to 255, base line = 128.
        /// if bitsPerSamples == 16, 2 bytes represent single short value(little endian) from -32767 to 32767, base line = 0.
        /// if stereo (Channels = 2), data order is LRLR...
        /// </param>
        /// <param name="bufferSize">buffer size. use BytesPerSec if called every second.</param>
        public void Start(Action<byte[]> onRecieved, int bufferSize)
        {
            this.OnRecieved = onRecieved;
            
            // double buffering.
            for (int i = 0; i < 2; i++)
            {
                var data = new byte[bufferSize];
                var data_handle = GCHandle.Alloc(data, GCHandleType.Pinned);

                var header = new Win32.WaveHeader();
                header.lpData = data_handle.AddrOfPinnedObject();
                header.dwBufferLength = (uint)bufferSize;
                header.dwUser = GCHandle.ToIntPtr(data_handle);

                var header_ptr = Marshal.AllocHGlobal(Win32.WaveHeader.Size);
                Marshal.StructureToPtr(header, header_ptr, true);

                Win32.waveInPrepareHeader(Device, header_ptr, Win32.WaveHeader.Size);
                Win32.waveInAddBuffer(Device, header_ptr, Win32.WaveHeader.Size);
            }

            int rc = Win32.waveInStart(Device);
            if (rc != Win32.MMSYSERR_NOERROR)
            {
                var sb = new StringBuilder(256);
                Win32.waveInGetErrorText(rc, sb, sb.Capacity);
                throw new InvalidOperationException(sb.ToString());
            }

            Active = true;
        }

        /// <summary>stop sampling after buffer becomes full.</summary>
        public void Stop()
        {
            Active = false;
        }

        /// <summary>stop sampling immediately.</summary>
        public void Reset()
        {
            Stop();

            // stops input and resets the current position to zero.
            // All pending buffers are marked as done and returned to the application.
            Win32.waveInReset(Device);
        }

        public void Close()
        {
            Win32.waveInClose(Device);
            Device = IntPtr.Zero;
        }

        public static string[] FindDevices()
        {
            uint devs = Win32.waveInGetNumDevs();
            string[] devNames = new string[devs];
            for (uint i = 0; i < devs; i++)
            {
                var caps = new Win32.WaveInCaps();
                Win32.waveInGetDevCaps(i, out caps, Win32.WaveInCaps.Size);
                devNames[i] = caps.szPname;
            }
            return devNames;
        }
    }

    class AudioOutput
    {
        // [How to use]
        // string[] devices = AudioOutput.FindDevices();
        // if (devices.Length == 0) return; // no device.

        // create AudioOutput with default device.
        // var device = new AudioOutput(44100, 16, 1);

        // start writing.
        // device.WriteStart(() =>
        // {
        //     // called when each buffer becomes empty and request more data.
        //     return sign_wave;
        // });

        // this.FormClosing += (s, ev) =>
        // {
        //     device.WriteStop();
        //     device.Close();
        // };

        private IntPtr Device;

        /// <summary>data bytes required per second.</summary>
        public readonly int BytesPerSec;

        private const uint WAVE_MAPPER = 0xFFFFFFFF;

        /// <summary>create AudioOutput with default device.</summary>
        public AudioOutput(int samplesPerSec, int bitsPerSamples, int channels) : this(samplesPerSec, bitsPerSamples, channels, WAVE_MAPPER) { }

        /// <summary>create AudioOutput with specified device.</summary>
        /// <param name="deviceId">index of FindDevices result.</param>
        public AudioOutput(int samplesPerSec, int bitsPerSamples, int channels, uint deviceId)
        {
            var format = new Win32.WaveFormatEx(samplesPerSec, bitsPerSamples, channels);
            BytesPerSec = samplesPerSec * channels * (bitsPerSamples / 8);

            WaveOutProc = WaveOutProcAndThreadStart();

            int rc = Win32.waveOutOpen(out Device, deviceId, ref format, WaveOutProc, IntPtr.Zero, Win32.CALLBACK_FUNCTION);
            if (rc != Win32.MMSYSERR_NOERROR)
            {
                var sb = new StringBuilder(256);
                Win32.waveOutGetErrorText(rc, sb, sb.Capacity);
                throw new InvalidOperationException(sb.ToString());
            }            
        }

        private Win32.WaveOutProc WaveOutProc;
        private Win32.WaveOutProc WaveOutProcAndThreadStart()
        {
            var proc_to_thread_event = new System.Threading.AutoResetEvent(false);
            var proc_to_thread_param = new Queue<IntPtr>();

            // callback
            var proc = new Win32.WaveOutProc((hwo, msg, user, param1, param2) =>
            {
                // [waveOutProc callback function remarks]
                // Applications should not call any system-defined functions from inside a callback function, except for
                // EnterCriticalSection, LeaveCriticalSection, midiOutLongMsg, midiOutShortMsg, OutputDebugString,
                // PostMessage, PostThreadMessage, SetEvent, timeGetSystemTime, timeGetTime, timeKillEvent, and timeSetEvent.
                // Calling other wave functions will cause deadlock.

                // Done:the device driver is finished with a data block
                if (msg == Win32.WaveOutMessage.Done)
                {
                    lock (proc_to_thread_param)
                    {
                        proc_to_thread_param.Enqueue(param1);
                    }

                    proc_to_thread_event.Set();
                }
            });

            // thread
            var thread = new System.Threading.Thread(() =>
            {
                IntPtr header_ptr;

                while (true)
                {                    
                    proc_to_thread_event.WaitOne(); // wait until event is rased.
                    {
                        lock (proc_to_thread_param)
                        {
                            header_ptr = proc_to_thread_param.Dequeue();
                        }
                        
                        Win32.waveOutUnprepareHeader(Device, header_ptr, Win32.WaveHeader.Size);

                        var header = Win32.WaveHeader.FromIntPtr(header_ptr);
                        var data_handle = GCHandle.FromIntPtr(header.dwUser);
                        data_handle.Free();

                        Marshal.FreeHGlobal(header_ptr);
                    }

                    var func = DataSupplier;
                    if (func != null)
                    {
                        var data = func();
                        if (data != null) Write(data);
                    }
                }
            });
            thread.Name = "WaveOutThread";
            thread.IsBackground = true;
            thread.Start();

            return proc;
        }

        private void WriteBuffer(IntPtr hwo, byte[] buffer)
        {
            var size = buffer.Length;

            var data = new byte[size];
            buffer.CopyTo(data, 0);
            var data_handle = GCHandle.Alloc(data, GCHandleType.Pinned);

            var header = new Win32.WaveHeader();
            header.lpData = data_handle.AddrOfPinnedObject();
            header.dwBufferLength = (uint)size;
            header.dwUser = GCHandle.ToIntPtr(data_handle);

            var header_ptr = Marshal.AllocHGlobal(Win32.WaveHeader.Size);
            Marshal.StructureToPtr(header, header_ptr, true);

            Win32.waveOutPrepareHeader(hwo, header_ptr, Win32.WaveHeader.Size);
            Win32.waveOutWrite(hwo, header_ptr, Win32.WaveHeader.Size);
        }

        /// <summary>write sound buffer to output device.</summary>
        public void Write(byte[] bytes)
        {
            WriteBuffer(Device, bytes);
        }

        private Func<byte[]> DataSupplier;

        /// <summary>start writing sound buffer with data supplier.</summary>
        /// <param name="dataSupplier">
        /// called when each buffer becomes empty and request more data.
        /// You have to supply extra data by dataSupplier result value.
        /// </param>
        public void WriteStart(Func<byte[]> dataSupplier)
        {
            WriteStart(dataSupplier, 2); // 2 = double buffering.
        }


        /// <summary>start writing sound buffer with data supplier.</summary>
        /// <param name="dataSupplier">
        /// called when each buffer becomes empty and request more data.
        /// You have to supply extra data by dataSupplier result value.
        /// </param>
        /// <param name="bufferDepth">
        /// internal buffer queue depth. 2 means double buffering.
        /// increse when supplied data length is too short and sound breaks.
        /// </param>
        public void WriteStart(Func<byte[]> dataSupplier, int bufferDepth)
        {
            DataSupplier = dataSupplier;

            for (int i = 0; i < bufferDepth; i++)
            {
                var buffer = dataSupplier();
                Write(buffer);
            }
        }

        /// <summary>stop calling data supplier after WriteStart.</summary>
        public void WriteStop()
        {
            DataSupplier = null;
        }

        public void Close()
        {
            Win32.waveOutClose(Device);
            Device = IntPtr.Zero;
        }

        public static string[] FindDevices()
        {
            var nums = Win32.waveOutGetNumDevs();
            var result = new string[nums];
            for (uint i = 0; i < nums; i++)
            {
                var caps = new Win32.WaveOutCaps();
                Win32.waveOutGetDevCaps(i, out caps, Win32.WaveOutCaps.Size);
                result[i] = caps.szPname;
            }
            return result;
        }
    }

    static class Win32
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct WaveFormatEx
        {
            public short wFormatTag;
            public short nChannels;
            public int   nSamplesPerSec;
            public int   nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;

            public WaveFormatEx(int SamplesPerSec, int BitsPerSample, int Channels)
            {
                const short WAVE_FORMAT_PCM = 1;
                wFormatTag     = WAVE_FORMAT_PCM;
                nSamplesPerSec = SamplesPerSec;
                nChannels      = (short)Channels;
                wBitsPerSample = (short)BitsPerSample;
                nBlockAlign    = (short)(Channels * BitsPerSample / 8);
                nAvgBytesPerSec = SamplesPerSec * nBlockAlign;
                cbSize = 0;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct WaveHeader
        {
            public IntPtr lpData;         // 波形データが格納されているメモリ領域を指すポインタ
            public uint dwBufferLength;   // 波形データのバイト数
            public uint dwBytesRecorded;  // 録音したデータのバイト数が格納されます。 WAVEを再生する場合は、このメンバは使用しません。
            public IntPtr dwUser;         // 固有データ
            public uint dwFlags;          // dwLoopsを初期化する場合は、アプリケーションがWHDR_BEGINLOOPとWHDR_ENDLOOPを指定します。
            public uint dwLoops;          // ループ再生をする回数を指定します。通常はループはコールバックで実現する。
            public IntPtr lpNext;         // 使用しない。
            public IntPtr reserved;       // 使用しない。
            public static readonly int Size = Marshal.SizeOf(typeof(WaveHeader));
            public static WaveHeader FromIntPtr(IntPtr p)
            {
                return (WaveHeader)Marshal.PtrToStructure(p, typeof(WaveHeader));
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct WaveOutCaps
        {
            public ushort wMid; // Manufacturer identifier
            public ushort wPid; // Product identifier 
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public CapsFormat dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public CapsSupport dwSupport;
            public static readonly int Size = Marshal.SizeOf(typeof(WaveOutCaps));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct WaveInCaps
        {
            public ushort wMid; // Manufacturer identifier
            public ushort wPid; // Product identifier 
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public CapsFormat dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public static readonly int Size = Marshal.SizeOf(typeof(WaveInCaps));
        }

        [Flags]
        public enum CapsFormat : uint
        {
            WAVE_INVALIDFORMAT = 0x00000000,  // invalid format
            WAVE_FORMAT_1M08 = 0x00000001,    // 11.025 kHz, Mono,   8-bit
            WAVE_FORMAT_1S08 = 0x00000002,    // 11.025 kHz, Stereo, 8-bit
            WAVE_FORMAT_1M16 = 0x00000004,    // 11.025 kHz, Mono,   16-bit
            WAVE_FORMAT_1S16 = 0x00000008,    // 11.025 kHz, Stereo, 16-bit
            WAVE_FORMAT_2M08 = 0x00000010,    // 22.05  kHz, Mono,   8-bit
            WAVE_FORMAT_2S08 = 0x00000020,    // 22.05  kHz, Stereo, 8-bit
            WAVE_FORMAT_2M16 = 0x00000040,    // 22.05  kHz, Mono,   16-bit
            WAVE_FORMAT_2S16 = 0x00000080,    // 22.05  kHz, Stereo, 16-bit
            WAVE_FORMAT_4M08 = 0x00000100,    // 44.1   kHz, Mono,   8-bit
            WAVE_FORMAT_4S08 = 0x00000200,    // 44.1   kHz, Stereo, 8-bit
            WAVE_FORMAT_4M16 = 0x00000400,    // 44.1   kHz, Mono,   16-bit
            WAVE_FORMAT_4S16 = 0x00000800,    // 44.1   kHz, Stereo, 16-bit

            WAVE_FORMAT_44M08 = 0x00000100,   // 44.1   kHz, Mono,   8-bit
            WAVE_FORMAT_44S08 = 0x00000200,   // 44.1   kHz, Stereo, 8-bit
            WAVE_FORMAT_44M16 = 0x00000400,   // 44.1   kHz, Mono,   16-bit
            WAVE_FORMAT_44S16 = 0x00000800,   // 44.1   kHz, Stereo, 16-bit
            WAVE_FORMAT_48M08 = 0x00001000,   // 48     kHz, Mono,   8-bit
            WAVE_FORMAT_48S08 = 0x00002000,   // 48     kHz, Stereo, 8-bit
            WAVE_FORMAT_48M16 = 0x00004000,   // 48     kHz, Mono,   16-bit
            WAVE_FORMAT_48S16 = 0x00008000,   // 48     kHz, Stereo, 16-bit
            WAVE_FORMAT_96M08 = 0x00010000,   // 96     kHz, Mono,   8-bit
            WAVE_FORMAT_96S08 = 0x00020000,   // 96     kHz, Stereo, 8-bit
            WAVE_FORMAT_96M16 = 0x00040000,   // 96     kHz, Mono,   16-bit
            WAVE_FORMAT_96S16 = 0x00080000,   // 96     kHz, Stereo, 16-bit
        }

        [Flags]
        public enum CapsSupport : uint
        {
            WAVECAPS_PITCH          = 0x0001, // supports pitch control
            WAVECAPS_PLAYBACKRATE   = 0x0002, // supports playback rate control
            WAVECAPS_VOLUME         = 0x0004, // supports volume control
            WAVECAPS_LRVOLUME       = 0x0008, // separate left-right volume control
            WAVECAPS_SYNC           = 0x0010,
            WAVECAPS_SAMPLEACCURATE = 0x0020,
        }

        public const int MMSYSERR_NOERROR     = 0x00; // エラーなし。
        public const int MMSYSERR_BADDEVICEID = 0x02; // 指定されたデバイス識別子は範囲外です。
        public const int MMSYSERR_ALLOCATED   = 0x04; // 指定されたリソースはすでに割り当てられています。
        public const int MMSYSERR_INVALHANDLE = 0x05; // デバイスハンドルが無効である
        public const int MMSYSERR_NODRIVER    = 0x06; // デバイスドライバが存在しません。
        public const int MMSYSERR_NOMEM       = 0x07; // メモリを割り当てられないか、またはロックできません。
        public const int MMSYSERR_HANDLEBUSY  = 0x0C; // 別のスレッドがハンドルを使用中である
        public const int WAVERR_BADFORMAT     = 0x20; // サポートされていないウェーブフォームオーディオ形式でオープンしようとしました。
        public const int WAVERR_STILLPLAYING  = 0x21; // キュー内にバッファがまだ残っている
        public const int WAVERR_UNPREPARED    = 0x22; // バッファが準備されていない

        public const int CALLBACK_WINDOW = 0x00010000;
        public const int CALLBACK_THREAD = 0x00020000;
        public const int CALLBACK_FUNCTION = 0x00030000;

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WaveFormatEx pwfx, WaveOutProc/*IntPtr*/ dwCallback, IntPtr dwCallbackInstance, int fdwOpen);

        public delegate void WaveOutProc(IntPtr hdrvr, WaveOutMessage uMsg, IntPtr dwUser, IntPtr/*int*/ dwParam1, int dwParam2);
        public enum WaveOutMessage { Open = 0x3BB, Close = 0x3BC, Done = 0x3BD }

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutGetErrorText(int mmrError, StringBuilder pszText, int cchtext);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutGetDevCaps(uint uDeviceID, out WaveOutCaps pwoc, int cbwoc);

        public delegate void WaveInProc(IntPtr hdrvr, WaveInMessage uMsg, IntPtr dwUser, IntPtr/*int*/ dwParam1, int dwParam2);
        public enum WaveInMessage { Open = 0x3BE, Close = 0x3BF, Data = 0x3C0 }

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInGetErrorText(int mmrError, StringBuilder pszText, int cchtext);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInGetDevCaps(uint uDeviceID, out WaveInCaps pwic, int cbwic);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WaveFormatEx pwfx, WaveInProc/*IntPtr*/ dwCallback, IntPtr dwCallbackInstance, int fdwOpen);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInPrepareHeader(IntPtr hwi, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInAddBuffer(IntPtr hwi, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInUnprepareHeader(IntPtr hwi, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInClose(IntPtr hwi);
    }
}
