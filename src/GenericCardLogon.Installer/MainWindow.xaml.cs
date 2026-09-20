using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace GenericCardLogon.Installer
{
    public partial class MainWindow : Window
    {
        private const string ServiceName = "GenericCardLogonService";
        private const string InstallDir = @"C:\Program Files\GenericCardLogon";
        private const string RegistryPath = @"SOFTWARE\GenericCardLogon";
        private readonly string _selfPath = Process.GetCurrentProcess().MainModule.FileName;

        public MainWindow()
        {
            InitializeComponent();
            MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            if (!IsAdministrator()) StatusText.Text = "管理者権限が必要です。EXEを右クリックして「管理者として実行」してください。";
        }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IsAdministrator()) throw new InvalidOperationException("管理者権限で実行してください。");
                Install();
                StatusText.Text = "インストールが完了しました。デスクトップにショートカットを作成しました。";
                MessageBox.Show("GenericCardLogon のインストールが完了しました。\n\nデスクトップの「GenericCardLogon Manager」からカード登録を開始できます。", "GenericCardLogon", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "インストールに失敗しました。";
                MessageBox.Show(ex.Message, "GenericCardLogon セットアップ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Install()
        {
            Directory.CreateDirectory(InstallDir);
            StopAndDeleteService();
            ExtractPayload("GenericCardLogon.Core.dll");
            ExtractPayload("GenericCardLogon.Service.exe");
            ExtractPayload("GenericCardLogon.Service.exe.config");
            ExtractPayload("PCSC.dll");
            ExtractPayload("PCSC.Iso7816.dll");
            ExtractPayload("GenericCardLogon.Manager.exe");
            ExtractPayload("GenericCardLogon.Manager.exe.config");

            var cpTarget = Path.Combine(Environment.SystemDirectory, "GenericCardLogon.CredentialProvider.dll");
            ExtractPayload("GenericCardLogon.CredentialProvider.dll", cpTarget);
            Run(Path.Combine(Environment.SystemDirectory, "regsvr32.exe"), "/s \"" + cpTarget + "\"");

            using (var key = Registry.LocalMachine.CreateSubKey(RegistryPath))
            {
                key.SetValue("ProviderLabel", "GenericCardLogon", RegistryValueKind.String);
                key.SetValue("Instruction", "ICカードをかざしてください", RegistryValueKind.String);
            }

            var serviceExe = Path.Combine(InstallDir, "GenericCardLogon.Service.exe");
            Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "create " + ServiceName + " binPath= \"" + serviceExe + "\" start= auto DisplayName= \"GenericCardLogon Service\"");
            Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "description " + ServiceName + " \"GenericCardLogon FeliCa authentication service\"");
            Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start " + ServiceName);

            var installedInstaller = Path.Combine(InstallDir, "GCL-Installer.exe");
            if (!string.Equals(Path.GetFullPath(_selfPath), Path.GetFullPath(installedInstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(_selfPath, installedInstaller, true);

            CreateShortcuts(installedInstaller);
        }

        private void StopAndDeleteService()
        {
            RunIgnoreFailure(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop " + ServiceName);
            System.Threading.Thread.Sleep(500);
            RunIgnoreFailure(Path.Combine(Environment.SystemDirectory, "sc.exe"), "delete " + ServiceName);
            System.Threading.Thread.Sleep(500);
        }

        private void ExtractPayload(string fileName, string target = null)
        {
            var destination = target ?? Path.Combine(InstallDir, fileName);
            var resource = "Payload." + fileName;
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (input == null) throw new FileNotFoundException("埋め込みファイルが見つかりません: " + fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (var output = File.Create(destination)) input.CopyTo(output);
            }
        }

        private static void Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(exe) + " failed (" + p.ExitCode + ").\n" + stderr + stdout);
            }
        }

        private static void RunIgnoreFailure(string exe, string args)
        {
            try { Run(exe, args); } catch { }
        }

        private static void CreateShortcuts(string installerPath)
        {
            var manager = Path.Combine(InstallDir, "GenericCardLogon.Manager.exe");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "GenericCardLogon");
            Directory.CreateDirectory(start);
            CreateShortcut(Path.Combine(desktop, "GenericCardLogon Manager.lnk"), manager, "FeliCaカード登録・設定");
            CreateShortcut(Path.Combine(desktop, "GenericCardLogon Installer.lnk"), installerPath, "GenericCardLogonのインストール・修復");
            CreateShortcut(Path.Combine(start, "GenericCardLogon Manager.lnk"), manager, "FeliCaカード登録・設定");
            CreateShortcut(Path.Combine(start, "GenericCardLogon Installer.lnk"), installerPath, "GenericCardLogonのインストール・修復");
        }

        private static void CreateShortcut(string path, string target, string description)
        {
            // Use WScript.Shell through reflection to avoid an extra runtime binder dependency.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("WScript.Shell が利用できません。");

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { path });

                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { target });
                shortcutType.InvokeMember(
                    "WorkingDirectory",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { Path.GetDirectoryName(target) });
                shortcutType.InvokeMember(
                    "Description",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { description });
                shortcutType.InvokeMember(
                    "IconLocation",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { target + ",0" });
                shortcutType.InvokeMember(
                    "Save",
                    BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            finally
            {
                if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private void LaunchManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = Path.Combine(InstallDir, "GenericCardLogon.Manager.exe");
                if (!File.Exists(path)) throw new FileNotFoundException("Managerがまだインストールされていません。");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "GenericCardLogon", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IsAdministrator()) throw new InvalidOperationException("管理者権限で実行してください。");
                if (MessageBox.Show("GenericCardLogonをアンインストールしますか？\n登録カードDBは保持します。", "GenericCardLogon", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                StopAndDeleteService();
                var cp = Path.Combine(Environment.SystemDirectory, "GenericCardLogon.CredentialProvider.dll");
                RunIgnoreFailure(Path.Combine(Environment.SystemDirectory, "regsvr32.exe"), "/u /s \"" + cp + "\"");
                DeleteShortcuts();
                StatusText.Text = "アンインストール処理が完了しました。カードDBは保持されています。";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "GenericCardLogon", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private static void DeleteShortcuts()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "GenericCardLogon");
            foreach (var p in new[] { Path.Combine(desktop, "GenericCardLogon Manager.lnk"), Path.Combine(desktop, "GenericCardLogon Installer.lnk"), Path.Combine(start, "GenericCardLogon Manager.lnk"), Path.Combine(start, "GenericCardLogon Installer.lnk") })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            try { if (Directory.Exists(start) && Directory.GetFiles(start).Length == 0) Directory.Delete(start); } catch { }
        }
    }
}
