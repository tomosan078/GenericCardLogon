using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GenericCardLogon.Manager
{
    /// <summary>
    /// Sony FeliCa Library (felica.dll) polling backend.
    /// This is intentionally loaded dynamically so the Manager still starts when
    /// the optional Sony FeliCa Library is unavailable.
    /// </summary>
    internal sealed class SonyFelicaPolling : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool InitializeLibraryDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool DisposeLibraryDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool OpenReaderWriterAutoDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool CloseReaderWriterDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetPollingTimeoutDelegate(uint milliseconds);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetRetryCountDelegate(uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool PollingAndGetCardInformationDelegate(
            IntPtr polling, ref byte numberOfCards, IntPtr cardInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct Polling
        {
            public IntPtr SystemCode;
            public byte TimeSlot;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CardInfo
        {
            public IntPtr CardIdm;
            public IntPtr CardPmm;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        private IntPtr _module;
        private InitializeLibraryDelegate _initialize;
        private DisposeLibraryDelegate _dispose;
        private OpenReaderWriterAutoDelegate _open;
        private CloseReaderWriterDelegate _close;
        private SetPollingTimeoutDelegate _setTimeout;
        private SetRetryCountDelegate _setRetry;
        private PollingAndGetCardInformationDelegate _polling;
        private bool _initialized;
        private bool _readerOpen;

        public static string DefaultDllPath
        {
            get
            {
                var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
                return Path.Combine(common, "Sony Shared", "FeliCaLibrary", "felica.dll");
            }
        }

        public bool IsAvailable { get; private set; }
        public string ErrorMessage { get; private set; }

        public SonyFelicaPolling()
        {
            try
            {
                var path = DefaultDllPath;
                if (!File.Exists(path))
                {
                    ErrorMessage = "Sony FeliCa Library が見つかりません: " + path;
                    return;
                }

                _module = LoadLibraryW(path);
                if (_module == IntPtr.Zero)
                {
                    ErrorMessage = "felica.dll をロードできません。Win32=" + Marshal.GetLastWin32Error();
                    return;
                }

                _initialize = LoadDelegate<InitializeLibraryDelegate>("initialize_library");
                _dispose = LoadDelegate<DisposeLibraryDelegate>("dispose_library");
                _open = LoadDelegate<OpenReaderWriterAutoDelegate>("open_reader_writer_auto");
                _close = LoadDelegate<CloseReaderWriterDelegate>("close_reader_writer");
                _setTimeout = LoadDelegate<SetPollingTimeoutDelegate>("set_polling_timeout");
                _setRetry = LoadDelegate<SetRetryCountDelegate>("set_retry_count");
                _polling = LoadDelegate<PollingAndGetCardInformationDelegate>("polling_and_get_card_information");

                if (!_initialize())
                {
                    ErrorMessage = "Sony FeliCa Library の初期化に失敗しました。";
                    return;
                }
                _initialized = true;

                if (!_open())
                {
                    ErrorMessage = "RC-S380をSony FeliCa Libraryで開けませんでした。";
                    return;
                }
                _readerOpen = true;
                // Apple Pay Express can be timing-sensitive. Use the same
                // conservative values used by the public FeliCa library
                // reference implementation: 200 ms polling timeout and 10 retries.
                _setTimeout(200);
                _setRetry(10);
                IsAvailable = true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private T LoadDelegate<T>(string name) where T : class
        {
            var p = GetProcAddress(_module, name);
            if (p == IntPtr.Zero)
                throw new EntryPointNotFoundException("felica.dll: " + name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>
        /// Poll the specified FeliCa system code and return the 8-byte IDm.
        /// Uses the legacy single-slot (0x00) behavior.
        /// </summary>
        public byte[] PollIdm(ushort systemCode)
        {
            return PollIdm(systemCode, 0x00);
        }

        /// <summary>
        /// Poll the specified FeliCa system code with an explicit FeliCa time slot.
        /// Valid values are 0x00, 0x01, 0x03, 0x07 and 0x0F.
        ///
        /// Express/Apple Pay detection is more timing-sensitive than ordinary
        /// physical FeliCa in some reader/card combinations, so the caller can
        /// deliberately try more than one prescribed slot without changing the
        /// Sony FeliCa Library ABI or adding a second reader backend.
        /// </summary>
        public byte[] PollIdm(ushort systemCode, byte timeSlot)
        {
            if (!IsAvailable)
                return null;

            IntPtr systemCodePtr = IntPtr.Zero;
            IntPtr idmPtr = IntPtr.Zero;
            IntPtr pmmPtr = IntPtr.Zero;
            IntPtr pollingPtr = IntPtr.Zero;
            IntPtr cardInfoPtr = IntPtr.Zero;

            try
            {
                // FeliCa Library expects network byte order for system_code.
                systemCodePtr = Marshal.AllocHGlobal(2);
                Marshal.WriteByte(systemCodePtr, 0, (byte)(systemCode >> 8));
                Marshal.WriteByte(systemCodePtr, 1, (byte)(systemCode & 0xFF));

                idmPtr = Marshal.AllocHGlobal(8);
                pmmPtr = Marshal.AllocHGlobal(8);
                Marshal.Copy(new byte[8], 0, idmPtr, 8);
                Marshal.Copy(new byte[8], 0, pmmPtr, 8);

                var polling = new Polling
                {
                    SystemCode = systemCodePtr,
                    TimeSlot = timeSlot
                };
                pollingPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Polling)));
                Marshal.StructureToPtr(polling, pollingPtr, false);

                var cardInfo = new CardInfo
                {
                    CardIdm = idmPtr,
                    CardPmm = pmmPtr
                };
                cardInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CardInfo)));
                Marshal.StructureToPtr(cardInfo, cardInfoPtr, false);

                byte numberOfCards = 0;
                if (!_polling(pollingPtr, ref numberOfCards, cardInfoPtr) || numberOfCards == 0)
                    return null;

                var idm = new byte[8];
                Marshal.Copy(idmPtr, idm, 0, 8);
                return idm;
            }
            finally
            {
                if (cardInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(cardInfoPtr);
                if (pollingPtr != IntPtr.Zero) Marshal.FreeHGlobal(pollingPtr);
                if (pmmPtr != IntPtr.Zero) Marshal.FreeHGlobal(pmmPtr);
                if (idmPtr != IntPtr.Zero) Marshal.FreeHGlobal(idmPtr);
                if (systemCodePtr != IntPtr.Zero) Marshal.FreeHGlobal(systemCodePtr);
            }
        }

        /// <summary>
        /// Apple Pay Express / Mobile FeliCa polling strategy.
        ///
        /// 0x0003 remains the required system code. We vary only the prescribed
        /// FeliCa time-slot value (4-slot, 2-slot, then 1-slot) so the same card
        /// can answer at a different RF response timing. This is intentionally
        /// limited to values defined by the FeliCa protocol; no undocumented
        /// Sony DLL entry points or raw reader commands are used.
        /// </summary>
        public byte[] PollExpressIdm()
        {
            if (!IsAvailable)
                return null;

            // Express Card polling is timing-sensitive. The documented Express
            // polling frame uses system code 0x0003 with time slot 0x00, so make
            // the single-slot poll the primary path instead of starting with a
            // multi-slot poll. Multi-slot values remain fallbacks for readers/cards
            // that behave differently.
            byte[] idm = PollIdm(0x0003, 0x00);
            if (idm != null) return idm;

            // Give the mobile FeliCa endpoint a short quiet gap before retrying.
            Thread.Sleep(60);
            idm = PollIdm(0x0003, 0x00);
            if (idm != null) return idm;

            idm = PollIdm(0x0003, 0x01);
            if (idm != null) return idm;

            idm = PollIdm(0x0003, 0x03);
            if (idm != null) return idm;

            // The caller controls the retry budget. Do not immediately start a
            // fourth RF cycle here.
            return null;
        }

        /// <summary>
        /// Generic physical FeliCa fallback using wildcard system code.
        /// Uses a 4-slot poll first, then the original single-slot behavior.
        /// </summary>
        public byte[] PollAnyFeliCaIdm()
        {
            if (!IsAvailable)
                return null;

            byte[] idm = PollIdm(0xFFFF, 0x03);
            if (idm != null) return idm;

            return PollIdm(0xFFFF, 0x00);
        }

        public void Dispose()
        {
            try
            {
                if (_readerOpen && _close != null)
                    _close();
            }
            catch { }
            finally
            {
                _readerOpen = false;
                if (_initialized && _dispose != null)
                {
                    try { _dispose(); } catch { }
                }
                _initialized = false;
                if (_module != IntPtr.Zero)
                {
                    FreeLibrary(_module);
                    _module = IntPtr.Zero;
                }
                IsAvailable = false;
            }
        }
    }
}
