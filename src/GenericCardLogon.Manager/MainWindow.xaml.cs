using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PCSC;
using PCSC.Iso7816;
using PCSC.Monitoring;
using GenericCardLogon.Core;

namespace GenericCardLogon.Manager
{
    public partial class MainWindow : Window
    {
        private static readonly string[] ReaderNameTokens =
        {
            "RC-S380",
            "Sony FeliCa Port/PaSoRi 2.0",
            "Sony FeliCa Port/PaSoRi 3.0",
            "Sony FeliCa Port/PaSoRi 4.0"
        };

        // Specification: FF CA 00 00 00 is the prototype IDm request.
        // RC-S380 + current driver/PCSC environment must be verified on real hardware.
        private static readonly byte[] IdmCommand = { 0xFF, 0xCA, 0x00, 0x00, 0x00 };

        private readonly ObservableCollection<CardRegistration> _registrations =
            new ObservableCollection<CardRegistration>();

        private ISCardMonitor _monitor;
        private string _readerName;
        private string _currentIdm;
        private string _currentIdmHash;
        private SonyFelicaPolling _sonyFelica;
        private CancellationTokenSource _pollCancellation;
        private CancellationTokenSource _registrationWaitCancellation;
        private string _lastDetectedIdm;
        private int _sonyMissCount;
        private readonly object _sonyPollLock = new object();

        public ObservableCollection<CardRegistration> Registrations
        {
            get { return _registrations; }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializePcscAsync();
            LoadUiSettings();
            StartSonyFelicaPolling();
            ReloadRegistrations();
        }


        private void LoadUiSettings()
        {
            ProviderLabelTextBox.Text = UiSettings.ProviderLabel;
            InstructionTextBox.Text = UiSettings.Instruction;
        }

        private void SaveUiSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiSettings.Save(ProviderLabelTextBox.Text, InstructionTextBox.Text);
                MessageText.Text = "LogonUIの表示設定を保存しました。次回ログオン画面の生成時から反映されます。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "GenericCardLogon", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task InitializePcscAsync()
        {
            try
            {
                string[] readers;

                using (var context = ContextFactory.Instance.Establish(SCardScope.System))
                {
                    readers = context.GetReaders();
                }

                var rcS380 = readers == null
                    ? null
                    : readers.FirstOrDefault(IsRcS380);

                if (string.IsNullOrWhiteSpace(rcS380))
                {
                    ReaderStatusText.Text = "RC-S380 未接続";
                    CardStatusText.Text = "RC-S380にカードリーダーを接続してください";

                    var readerList = readers == null || readers.Length == 0
                        ? "（PC/SC readerが0件です）"
                        : string.Join("\r\n", readers.Select(x => "・" + x));

                    MessageText.Text =
                        "PC/SC reader一覧にRC-S380系が見つかりません。\r\n" +
                        "検出されたreader:\r\n" + readerList + "\r\n\r\n" +
                        "Sony NFCポートソフトウェアのPC/SC機能が有効か確認してください。";
                    return;
                }

                _readerName = rcS380;
                ReaderStatusText.Text = "接続中: " + _readerName;
                MessageText.Text = "RC-S380を確認しました。Apple Pay Express優先で初期化します。";
            }
            catch (Exception ex)
            {
                ReaderStatusText.Text = "初期化エラー";
                MessageText.Text = ex.Message;
            }

            await Task.CompletedTask;
        }

        private static bool IsRcS380(string readerName)
        {
            if (string.IsNullOrWhiteSpace(readerName))
                return false;

            return ReaderNameTokens.Any(token =>
                readerName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void StartSonyFelicaPolling()
        {
            try
            {
                _sonyFelica = new SonyFelicaPolling();
                if (!_sonyFelica.IsAvailable)
                {
                    StartPcscMonitorFallback();
                    MessageText.Text = "Sony FeliCa Libraryが利用できないため、PC/SCフォールバックで待機します。\r\n" + _sonyFelica.ErrorMessage;
                    return;
                }

                // IMPORTANT: do not run PC/SC card monitoring at the same time as
                // Sony FeliCa Library. Both can compete for the RC-S380/Port-100
                // device and make Apple Pay Express detection intermittent.
                _pollCancellation = new CancellationTokenSource();
                var token = _pollCancellation.Token;
                var rfQuietUntilUtc = DateTime.MinValue;

                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        string idmHex = null;
                        try
                        {
                            if (DateTime.UtcNow < rfQuietUntilUtc)
                            {
                                await Task.Delay(100, token);
                                continue;
                            }

                            lock (_sonyPollLock)
                            {
                                // Apple Pay Express Suica: explicitly poll 0x0003
                                // with several prescribed FeliCa time-slot values.
                                // This is the critical path; do not fall back to a
                                // generic UID or 4-byte NFCID1.
                                var idm = _sonyFelica.PollExpressIdm();
                                if (idm != null && idm.Length == 8)
                                    idmHex = BitConverter.ToString(idm).Replace("-", "");

                                // For a physical FeliCa card, use wildcard polling
                                // only when the Apple Pay/Suica poll did not find one.
                                if (idmHex == null)
                                {
                                    idm = _sonyFelica.PollAnyFeliCaIdm();
                                    if (idm != null && idm.Length == 8)
                                        idmHex = BitConverter.ToString(idm).Replace("-", "");
                                }
                            }

                            if (!string.IsNullOrEmpty(idmHex))
                            {
                                _sonyMissCount = 0;
                                if (!string.Equals(idmHex, _lastDetectedIdm, StringComparison.OrdinalIgnoreCase))
                                {
                                    _lastDetectedIdm = idmHex;
                                    // Stop reader activity immediately after a successful
                                    // detection. This gives Apple Watch Express Mode a
                                    // quiet RF window while its screen transitions.
                                    rfQuietUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
                                    await Dispatcher.InvokeAsync(() => AcceptPolledIdm(idmHex));
                                }
                            }
                            else
                            {
                                // Require several consecutive misses before clearing
                                // the UI. This prevents a brief RF gap from making
                                // Express detection appear to fail.
                                _sonyMissCount++;
                                if (_sonyMissCount >= 5)
                                {
                                    _sonyMissCount = 0;
                                    if (_lastDetectedIdm != null)
                                    {
                                        _lastDetectedIdm = null;
                                        await Dispatcher.InvokeAsync(() => ClearCard("カードを離しました。再度かざしてください。"));
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _sonyMissCount++;
                            if (_sonyMissCount >= 5)
                            {
                                _sonyMissCount = 0;
                                await Dispatcher.InvokeAsync(() => MessageText.Text = "FeliCa Polling待機中: " + ex.Message);
                            }
                        }

                        try { await Task.Delay(80, token); }
                        catch (TaskCanceledException) { break; }
                    }
                }, token);

                ReaderStatusText.Text = "接続中 / Apple Pay Express優先";
                CardStatusText.Text = "Apple Pay Express / FeliCaカードをかざしてください";
                MessageText.Text = "Sony FeliCa Libraryで0x0003を優先し、タイムスロットを切り替えてPolling中です。PC/SC監視との競合は停止しています。";
            }
            catch (Exception ex)
            {
                StartPcscMonitorFallback();
                MessageText.Text = "Sony FeliCa Polling初期化エラー: " + ex.Message;
            }
        }

        private void StartPcscMonitorFallback()
        {
            if (string.IsNullOrWhiteSpace(_readerName) || _monitor != null)
                return;

            try
            {
                _monitor = MonitorFactory.Instance.Create(SCardScope.System);
                _monitor.CardInserted += Monitor_CardInserted;
                _monitor.CardRemoved += Monitor_CardRemoved;
                _monitor.MonitorException += Monitor_MonitorException;
                _monitor.Start(_readerName);
            }
            catch (Exception ex)
            {
                MessageText.Text = "PC/SC監視開始エラー: " + ex.Message;
            }
        }

        private void AcceptPolledIdm(string idm)
        {
            try
            {
                _currentIdm = idm;
                _currentIdmHash = CardHash.ComputeHash(idm);
                CardHashTextBox.Text = _currentIdmHash;
                CardStatusText.Text = "FeliCa / Apple Pay Expressを検出しました";
                RegisterButton.IsEnabled = !string.IsNullOrWhiteSpace(UserNameTextBox.Text);
                MessageText.Text = "8バイトのFeliCa IDmを取得しました。Apple Pay Expressも同じ登録フローで登録できます。";
            }
            catch (Exception ex)
            {
                ClearCard("FeliCa PollingのIDm処理エラー: " + ex.Message);
            }
        }

        private async void Monitor_CardInserted(object sender, CardStatusEventArgs e)
        {
            if (_sonyFelica != null && _sonyFelica.IsAvailable)
                return;
            if (!IsRcS380(e.ReaderName))
                return;

            await Dispatcher.InvokeAsync(async () =>
            {
                CardStatusText.Text = "カード検出。IDmを取得しています...";
                RegisterButton.IsEnabled = false;

                try
                {
                    var idm = await Task.Run(() => ReadIdm(e.ReaderName));

                    if (string.IsNullOrWhiteSpace(idm))
                    {
                        ClearCard("FeliCa IDmを取得できませんでした。");
                        return;
                    }

                    _currentIdm = idm;
                    _currentIdmHash = CardHash.ComputeHash(idm);

                    CardHashTextBox.Text = _currentIdmHash;
                    CardStatusText.Text = "FeliCaカードを検出しました";
                    RegisterButton.IsEnabled = !string.IsNullOrWhiteSpace(UserNameTextBox.Text);
                    MessageText.Text = "IDm Hashを取得しました。IDmそのものは表示しません。";
                }
                catch (Exception ex)
                {
                    ClearCard("IDm取得エラー: " + ex.Message);
                }
            });
        }

        private void Monitor_CardRemoved(object sender, CardStatusEventArgs e)
        {
            if (_sonyFelica != null && _sonyFelica.IsAvailable)
                return;
            if (!IsRcS380(e.ReaderName))
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ClearCard("カードが離れました。");
            }));
        }

        private void Monitor_MonitorException(object sender, PCSC.Exceptions.PCSCException ex)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageText.Text = "PC/SC監視エラー: " + ex.Message;
            }));
        }

        private string ReadIdm(string readerName)
        {
            // Prefer the Sony FeliCa Library because Apple Pay Express can bypass
            // generic PC/SC card detection and requires an actual FeliCa Polling.
            try
            {
                using (var sony = new SonyFelicaPolling())
                {
                    if (sony.IsAvailable)
                    {
                        var idm = sony.PollExpressIdm() ?? sony.PollAnyFeliCaIdm();
                        if (idm != null && idm.Length == 8)
                            return BitConverter.ToString(idm).Replace("-", "");
                    }
                }
            }
            catch
            {
                // Fall back to the existing PC/SC path below.
            }

            using (var context = ContextFactory.Instance.Establish(SCardScope.System))
            using (var reader = new IsoReader(
                context,
                readerName,
                SCardShareMode.Shared,
                SCardProtocol.Any,
                false))
            {
                // First try the standard PC/SC UID command.
                // For a FeliCa card this normally returns the 8-byte IDm.
                var uidResponse = TransmitCase2(
                    reader,
                    0xFF, 0xCA, 0x00, 0x00, 0x00);

                if (uidResponse.SW1 == 0x90 && uidResponse.SW2 == 0x00)
                {
                    var uidData = uidResponse.GetData();

                    if (uidData != null && uidData.Length >= 8)
                        return BitConverter.ToString(uidData, 0, 8).Replace("-", "");
                }

                // Apple Pay / Express mode can expose the device differently from
                // a physical FeliCa card. Try a FeliCa Polling command through the
                // PC/SC direct-transmit pseudo APDU used by NFC readers.
                //
                // FeliCa Polling:
                // LEN=06, CMD=00, SystemCode=FFFF, RequestCode=00, TimeSlot=00
                var pollingResponse = TransmitCase4(
                    reader,
                    0xFF, 0x00, 0x00, 0x00,
                    new byte[] { 0x06, 0x00, 0xFF, 0xFF, 0x00, 0x00 },
                    0x00);

                if (pollingResponse.SW1 == 0x90 && pollingResponse.SW2 == 0x00)
                {
                    var pollingData = pollingResponse.GetData();

                    // FeliCa Polling response:
                    // LEN(0x12), ResponseCode(0x01), IDm(8), PMm(8)
                    if (pollingData != null &&
                        pollingData.Length >= 10 &&
                        pollingData[1] == 0x01)
                    {
                        return BitConverter.ToString(
                            pollingData, 2, 8).Replace("-", "");
                    }
                }

                // Collect the reader-reported card type for a useful diagnostic.
                string typeText = "取得できませんでした";
                var typeResponse = TransmitCase2(
                    reader,
                    0xFF, 0xCA, 0xF3, 0x00, 0x00);

                if (typeResponse.SW1 == 0x90 && typeResponse.SW2 == 0x00)
                {
                    var typeData = typeResponse.GetData();
                    if (typeData != null && typeData.Length > 0)
                    {
                        typeText = BitConverter.ToString(typeData).Replace("-", "");
                    }
                }

                var uidHex = "(データなし)";
                if (uidResponse.HasData)
                {
                    var d = uidResponse.GetData();
                    if (d != null && d.Length > 0)
                        uidHex = BitConverter.ToString(d).Replace("-", "");
                }

                throw new InvalidOperationException(
                    "FeliCaの8バイトIDmを取得できませんでした。\r\n" +
                    "PC/SC UID応答: " + uidHex + "\r\n" +
                    "カード種別情報: " + typeText + "\r\n\r\n" +
                    "Apple PayのSuicaをエクスプレスカードとして使用している場合、" +
                    "iPhone側がFeliCaカードとは異なるNFCデバイスとして応答することがあります。" +
                    "\r\n" +
                    "この場合、4バイト等の値をIDmとして登録せず、安全に登録を中止します。");
            }
        }

        private static Response TransmitCase2(
            IsoReader reader,
            byte cla,
            byte ins,
            byte p1,
            byte p2,
            byte le)
        {
            var apdu = new CommandApdu(IsoCase.Case2Short, reader.ActiveProtocol)
            {
                CLA = cla,
                Instruction = (InstructionCode)ins,
                P1 = p1,
                P2 = p2,
                Le = le
            };

            return reader.Transmit(apdu);
        }

        private static Response TransmitCase4(
            IsoReader reader,
            byte cla,
            byte ins,
            byte p1,
            byte p2,
            byte[] data,
            byte le)
        {
            var apdu = new CommandApdu(IsoCase.Case4Short, reader.ActiveProtocol)
            {
                CLA = cla,
                Instruction = (InstructionCode)ins,
                P1 = p1,
                P2 = p2,
                Data = data,
                Le = le
            };

            return reader.Transmit(apdu);
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            var userName = UserNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(userName))
            {
                MessageBox.Show(this, "Windowsユーザー名を入力してください。", "登録", MessageBoxButton.OK, MessageBoxImage.Warning);
                UserNameTextBox.Focus();
                return;
            }

            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show(this, "Windowsパスワードを入力してください。", "登録", MessageBoxButton.OK, MessageBoxImage.Warning);
                PasswordBox.Focus();
                return;
            }

            if (!VerifyWindowsPassword(userName, password))
            {
                MessageBox.Show(this, "Windowsユーザー名またはパスワードを確認できませんでした。", "登録", MessageBoxButton.OK, MessageBoxImage.Error);
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }

            RegisterButton.IsEnabled = false;
            RegistrationHintText.Text = "カードをRC-S380にかざしてください。最大10秒間、自動検出します。";
            CardStatusText.Text = "カードをかざしてください（登録待機中）";
            MessageText.Text = "Apple Pay ExpressはSuicaのシステムコード 0x0003 を優先してPollingしています。";

            try
            {
                _registrationWaitCancellation?.Cancel();
                _registrationWaitCancellation = new CancellationTokenSource();
                var token = _registrationWaitCancellation.Token;

                // Even if detection is momentarily missed, keep the registration
                // window alive for 10 seconds while the background poller retries.
                for (int i = 0; i < 100 && string.IsNullOrWhiteSpace(_currentIdmHash); i++)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(100, token);
                }

                if (string.IsNullOrWhiteSpace(_currentIdmHash))
                {
                    MessageBox.Show(this,
                        "カードを検出できませんでした。\r\n\r\n" +
                        "Apple Payの場合は、Suicaをエクスプレスカードに設定した状態で、iPhoneの上部をRC-S380の中央付近に近づけてください。\r\n" +
                        "もう一度「カードを登録」を押して再試行できます。",
                        "カード未検出", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var existing = _registrations.FirstOrDefault(
                    x => string.Equals(x.IdmHash, _currentIdmHash, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    MessageBox.Show(this,
                        "このカードはすでに登録されています。\r\nユーザー: " + existing.UserName,
                        "登録", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                byte[] protectedPassword;
                var passwordBytes = Encoding.Unicode.GetBytes(password);
                try
                {
                    protectedPassword = ProtectedData.Protect(passwordBytes, null, DataProtectionScope.LocalMachine);
                }
                finally
                {
                    Array.Clear(passwordBytes, 0, passwordBytes.Length);
                }

                var registration = new CardRegistration
                {
                    CardType = "FeliCa",
                    IdmHash = _currentIdmHash,
                    UserName = userName,
                    PasswordProtectedBase64 = Convert.ToBase64String(protectedPassword),
                    RegisteredAtUtc = DateTime.UtcNow
                };
                Array.Clear(protectedPassword, 0, protectedPassword.Length);

                new RegistrationStore().Add(registration);
                ReloadRegistrations();
                MessageText.Text = "カードを " + userName + " に登録しました。";
                RegistrationHintText.Text = "登録完了。カードログオンと通常のWindowsパスワード/PINによる復旧手段を併用できます。";
                UserNameTextBox.Clear();
                PasswordBox.Clear();
            }
            catch (OperationCanceledException)
            {
                MessageText.Text = "カード登録待機をキャンセルしました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "登録に失敗しました。\r\n\r\n" + ex.Message, "登録エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateRegisterButtonState();
                RegistrationHintText.Text = "ユーザー名とWindowsパスワードを入力してから登録を開始します。カード未検出でも登録ボタンから最大10秒待機できます。";
            }
        }

        private void UserNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateRegisterButtonState();
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdateRegisterButtonState();
        }

        private void UpdateRegisterButtonState()
        {
            if (RegisterButton == null || PasswordBox == null || UserNameTextBox == null) return;
            RegisterButton.IsEnabled = !string.IsNullOrWhiteSpace(UserNameTextBox.Text) && !string.IsNullOrEmpty(PasswordBox.Password);
        }

        private static bool VerifyWindowsPassword(string account, string password)
        {
            string domain = ".";
            string user = account.Trim();
            int slash = user.IndexOf('\\');
            if (slash > 0 && slash < user.Length - 1)
            {
                domain = user.Substring(0, slash);
                user = user.Substring(slash + 1);
            }
            IntPtr token;
            bool ok = LogonUser(user, domain, password, 2, 0, out token);
            if (ok) CloseHandle(token);
            return ok;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = RegistrationGrid.SelectedItem as CardRegistration;

            if (selected == null)
            {
                MessageBox.Show(
                    this,
                    "削除する登録を一覧から選択してください。",
                    "削除",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                this,
                string.Format(
                    "次の登録を削除しますか？\r\n\r\nユーザー: {0}\r\nIDm Hash: {1}",
                    selected.UserName,
                    selected.IdmHash),
                "登録削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                var store = new RegistrationStore();
                store.Delete(selected.IdmHash);

                ReloadRegistrations();
                MessageText.Text = "登録を削除しました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "削除に失敗しました。\r\n\r\n" + ex.Message,
                    "削除エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BackupButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "cards.jsonをバックアップ",
                FileName = "cards-backup.json",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                new RegistrationStore().Backup(dialog.FileName);
                MessageText.Text = "JSONバックアップを保存しました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "バックアップに失敗しました。\r\n\r\n" + ex.Message, "バックアップエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReloadRegistrationsButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadRegistrations();
            MessageText.Text = "登録一覧を更新しました。";
        }

        private void ReloadRegistrations()
        {
            try
            {
                var store = new RegistrationStore();
                var items = store.GetAll();

                _registrations.Clear();

                if (items != null)
                {
                    foreach (var item in items)
                        _registrations.Add(item);
                }

                RegistrationGrid.Items.Refresh();
            }
            catch (Exception ex)
            {
                MessageText.Text = "登録DBの読み込みに失敗しました: " + ex.Message;
            }
        }

        private void ClearCard(string message)
        {
            _sonyMissCount = 0;
            _currentIdm = null;
            _currentIdmHash = null;
            CardHashTextBox.Clear();
            CardStatusText.Text = "カードをかざしてください";
            RegisterButton.IsEnabled = !string.IsNullOrWhiteSpace(UserNameTextBox.Text);
            MessageText.Text = message;
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            try
            {
                if (_registrationWaitCancellation != null)
                {
                    _registrationWaitCancellation.Cancel();
                    _registrationWaitCancellation.Dispose();
                    _registrationWaitCancellation = null;
                }
                if (_pollCancellation != null)
                {
                    _pollCancellation.Cancel();
                    _pollCancellation.Dispose();
                    _pollCancellation = null;
                }
                if (_sonyFelica != null)
                {
                    _sonyFelica.Dispose();
                    _sonyFelica = null;
                }
            }
            catch { }

            try
            {
                if (_monitor != null)
                {
                    _monitor.CardInserted -= Monitor_CardInserted;
                    _monitor.CardRemoved -= Monitor_CardRemoved;
                    _monitor.MonitorException -= Monitor_MonitorException;
                    _monitor.Cancel();
                    _monitor.Dispose();
                    _monitor = null;
                }
            }
            catch
            {
                // Application shutdown must not be blocked by monitor cleanup.
            }
        }
    }
}
