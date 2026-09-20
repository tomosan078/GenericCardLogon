using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using GenericCardLogon.Core;
using System.ServiceProcess;

namespace GenericCardLogon.Service
{
    internal static class Program
    {
        public static void Main(string[] args)
        {
            if (Environment.UserInteractive)
            {
                using (var host = new ServiceHost())
                {
                    host.StartInteractive();
                    Console.WriteLine("GenericCardLogon Service is running. Press Enter to stop.");
                    Console.ReadLine();
                    host.StopHost();
                }
                return;
            }
            ServiceBase.Run(new ServiceBase[] { new WindowsService() });
        }
    }

    internal sealed class WindowsService : ServiceBase
    {
        private ServiceHost _host;
        protected override void OnStart(string[] args)
        {
            _host = new ServiceHost();
            _host.Start();
        }
        protected override void OnStop()
        {
            if (_host != null) _host.StopHost();
            _host = null;
        }
    }

    internal sealed class ServiceHost : IDisposable
    {
        private const string PipeName = "GenericCardLogon.Logon.v1";
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Thread _thread;
        private SonyFelicaPolling _sonyPolling;
        private readonly object _sonyPollingLock = new object();
        private readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GenericCardLogon", "service.log");

        public void Start() { StartCore(false); }
        public void StartInteractive() { StartCore(true); }
        private void StartCore(bool interactive)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
            Log("service start; interactive=" + interactive);
            // Keep the Sony FeliCa Library/RC-S380 session warm across AUTH requests.
            // Apple Pay Express can be timing-sensitive; reopening felica.dll and the
            // reader for every credential attempt adds avoidable startup latency.
            TryInitializeSonyPolling();
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }
        private void Run()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = CreatePipe())
                    {
                        pipe.WaitForConnection();
                        if (_stop.IsCancellationRequested) break;
                        Handle(pipe);
                    }
                }
                catch (Exception ex)
                {
                    Log("pipe error: " + ex);
                    Thread.Sleep(250);
                }
            }
        }

        private void TryInitializeSonyPolling()
        {
            lock (_sonyPollingLock)
            {
                if (_sonyPolling != null && _sonyPolling.IsAvailable) return;
                try
                {
                    _sonyPolling?.Dispose();
                    _sonyPolling = new SonyFelicaPolling();
                    Log(_sonyPolling.IsAvailable
                        ? "Sony FeliCa Library initialized; reader session kept warm."
                        : "Sony FeliCa Library unavailable: " + (_sonyPolling.ErrorMessage ?? "unknown"));
                }
                catch (Exception ex)
                {
                    _sonyPolling = null;
                    Log("Sony FeliCa initialization failed: " + ex);
                }
            }
        }

        private SonyFelicaPolling GetSonyPolling()
        {
            lock (_sonyPollingLock)
            {
                if (_sonyPolling == null || !_sonyPolling.IsAvailable)
                {
                    try
                    {
                        _sonyPolling?.Dispose();
                        _sonyPolling = new SonyFelicaPolling();
                    }
                    catch (Exception ex)
                    {
                        Log("Sony FeliCa reinitialization failed: " + ex);
                        _sonyPolling = null;
                    }
                }
                return _sonyPolling;
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
        }

        private void Handle(NamedPipeServerStream pipe)
        {
            if (!IsLogonUiClient(pipe))
            {
                WriteFailure(pipe, "許可されていないIPCクライアントです。");
                return;
            }
            var request = new byte[4];
            ReadExactly(pipe, request, 0, request.Length);
            if (Encoding.ASCII.GetString(request) != "AUTH")
            {
                WriteFailure(pipe, "不正な要求です。");
                return;
            }

            SonyFelicaPolling polling = GetSonyPolling();
            if (polling == null || !polling.IsAvailable)
            {
                WriteFailure(pipe, polling == null ? "RC-S380を利用できません。" : (polling.ErrorMessage ?? "RC-S380を利用できません。"));
                return;
            }

            byte[] idm = null;
            // Express/Apple Pay path: keep 0x0003 as the primary polling target.
            // Each attempt now tries prescribed FeliCa time-slot values (0x03,
            // 0x01, 0x00) to improve timing tolerance without changing the
            // reader backend or introducing raw Port-100 commands.
            // Express detection gets several independent RF cycles. PollExpressIdm
            // itself starts with the documented 0x0003/0x00 path, so this loop is
            // intentionally a retry loop rather than a long blocking single poll.
            for (int i = 0; i < 8 && idm == null; i++)
            {
                idm = polling.PollExpressIdm();
                if (idm == null) Thread.Sleep(100);
            }
            // IMPORTANT: once Express returns an IDm, leave this AUTH path
            // immediately. No additional Polling is performed after success.
            if (idm == null)
            {
                // Generic physical FeliCa fallback.
                for (int i = 0; i < 10 && idm == null; i++)
                {
                    idm = polling.PollAnyFeliCaIdm();
                    if (idm == null) Thread.Sleep(80);
                }
            }
            if (idm == null)
            {
                WriteFailure(pipe, "カードを検出できませんでした。");
                return;
            }

            var hash = CardHash.ComputeHash(ToHex(idm));
            var registration = new RegistrationStore().GetAll();
            var match = registration.FirstOrDefault(x => string.Equals(x.IdmHash, hash, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                WriteFailure(pipe, "このカードは登録されていません。");
                return;
            }

            byte[] password = null;
            try
            {
                password = ProtectedData.Unprotect(Convert.FromBase64String(match.PasswordProtectedBase64), null, DataProtectionScope.LocalMachine);
                WriteSuccess(pipe, match.UserName, password);
            }
            catch (Exception ex)
            {
                Log("credential decrypt failed: " + ex);
                WriteFailure(pipe, "登録された認証情報を復号できませんでした。");
            }
            finally
            {
                if (password != null) CryptographicOperationsCompat.Zero(password);
            }
        }

        private static void WriteSuccess(Stream stream, string userName, byte[] passwordUtf16)
        {
            var user = Encoding.UTF8.GetBytes(userName ?? string.Empty);
            if (user.Length > ushort.MaxValue || passwordUtf16.Length > ushort.MaxValue) throw new InvalidDataException();
            stream.WriteByte(0);
            WriteUInt16(stream, (ushort)user.Length);
            WriteUInt16(stream, (ushort)passwordUtf16.Length);
            stream.Write(user, 0, user.Length);
            stream.Write(passwordUtf16, 0, passwordUtf16.Length);
            stream.Flush();
        }
        private static void WriteFailure(Stream stream, string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message ?? "認証に失敗しました。");
            if (bytes.Length > ushort.MaxValue) Array.Resize(ref bytes, ushort.MaxValue);
            stream.WriteByte(1);
            WriteUInt16(stream, (ushort)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        private static void WriteUInt16(Stream s, ushort v) { s.WriteByte((byte)(v & 255)); s.WriteByte((byte)(v >> 8)); }
        private static void ReadExactly(Stream s, byte[] b, int o, int n)
        {
            while (n > 0)
            {
                int r = s.Read(b, o, n);
                if (r <= 0) throw new EndOfStreamException();
                o += r; n -= r;
            }
        }
        private static bool IsLogonUiClient(NamedPipeServerStream pipe)
        {
            try
            {
                uint pid;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)) return false;
                using (var p = Process.GetProcessById((int)pid))
                {
                    var expected = Path.Combine(Environment.SystemDirectory, "LogonUI.exe");
                    return string.Equals(p.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }
        private static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }
        private void Log(string text)
        {
            try { File.AppendAllText(_logPath, DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine); } catch { }
        }
        public void StopHost()
        {
            _stop.Cancel();
            try
            {
                using (var wake = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                { wake.Connect(100); }
            }
            catch { }
            if (_thread != null && _thread.IsAlive) _thread.Join(2000);
            // Dispose only after the worker thread has stopped so an in-flight
            // PollIdm call can never race with FreeLibrary/close_reader_writer.
            lock (_sonyPollingLock)
            {
                try { _sonyPolling?.Dispose(); } catch { }
                _sonyPolling = null;
            }
        }
        public void Dispose() { StopHost(); _stop.Dispose(); }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle Pipe, out uint ClientProcessId);
    }

    internal static class CryptographicOperationsCompat
    {
        public static void Zero(byte[] data) { if (data == null) return; Array.Clear(data, 0, data.Length); }
    }
}
