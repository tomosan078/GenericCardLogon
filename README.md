# GenericCardLogon RC-S380

Windows 11 x64向けの、**Sony PaSoRi RC-S380 + FeliCaカードを利用したWindowsログオン用Credential Provider**です。

> **Status: Experimental / Personal project**
>
> 実機（RC-S380、Suica、Apple Pay / Express Suica、通常FeliCa）での動作確認を行っている開発版です。Windowsのログオン機構に関係するため、導入前に必ず通常のパスワード/PINによるログオン手段と復旧手段を確保してください。

## 概要

GenericCardLogonは、FeliCaのIDmをカード識別子として利用し、登録済みのWindowsアカウントに対応するパスワードを取得して、Windows標準のCredential Provider経由でログオンする構成です。

```text
┌──────────────┐
│ RC-S380      │
│ PaSoRi       │
└──────┬───────┘
       │ FeliCa Polling
       ▼
┌──────────────────────┐
│ GenericCardLogon     │
│ Service (LocalSystem)│
└──────────┬───────────┘
           │ Restricted Named Pipe
           ▼
┌────────────────────────────┐
│ GenericCardLogon           │
│ Credential Provider (x64)  │
└────────────┬───────────────┘
             │ Standard Windows credential serialization
             ▼
┌────────────────────────────┐
│ Windows LogonUI            │
└────────────────────────────┘
```

通常のWindows Password / PIN Credential Providerは置き換えず、共存させます。

## 現在の仕様

- OS: Windows 11 x64
- Reader: Sony PaSoRi RC-S380
- FeliCa IDm: 8 bytes / 16 hexadecimal characters
- IDmは平文保存せず、`SHA-256("FeliCa:" + IDm)` を64文字hexで保存
- 登録DB: `%ProgramData%\GenericCardLogon\cards.json`
- Service: LocalSystem / 自動起動
- IPC: `GenericCardLogon.Logon.v1` Named Pipe
- Credential Provider: x64 / V2
- 保存パスワード: Windows DPAPI `LocalMachine` スコープで保護
- WebAuthn / FIDO2: **現在の版には含まれません**
- LSA Authentication Package: **追加・変更しません**

## 対応カード経路

### 通常のFeliCa / Suica

Sony FeliCa Libraryを優先してFeliCa Pollingを行います。

- 通常の物理FeliCaは `0xFFFF` をフォールバックとして使用
- Suica系は `0x0003` を優先
- FeliCa Libraryが利用できない場合は、既存のPC/SC経路にフォールバックする構成があります

### Apple Pay / Express Suica

Apple PayのExpressカードは通常のPC/SC `Get UID` だけでは期待する8-byte FeliCa IDmを取得できない場合があります。そのため本版ではSony FeliCa Libraryを使用したPolling経路を追加しています。

Express用Pollingは `0x0003` を対象とし、FeliCaのTime Slotを変えながら試行します。

現在の順序は概ね以下です。

```text
0x0003 / TimeSlot 0x00
        ↓
短い待機
        ↓
0x0003 / TimeSlot 0x00
        ↓
0x0003 / TimeSlot 0x01
        ↓
0x0003 / TimeSlot 0x03
```

Service側ではExpress検出を複数回の独立したRFサイクルとして再試行します。

**IDmを取得した時点で、そのAUTH要求について追加Pollingを行わず処理を次へ進めます。** これはApple Watch / Expressカード検出後もReaderへのPollingを継続してしまう状態を避けるための現在版の挙動です。

> Apple Pay / Express ModeそのものはApple Wallet側の機能です。WindowsアプリからExpress設定を変更するものではありません。

## カード登録

Managerから以下の流れで登録します。

1. Windowsユーザー名を入力
2. 現在のWindowsパスワードを入力
3. RC-S380にカードをかざす
4. FeliCa IDmを取得
5. `SHA-256("FeliCa:" + IDm)` を生成
6. WindowsパスワードをDPAPIで保護
7. `cards.json` にユーザーとの対応を保存

IDmそのものは登録DBには保存しません。

## ログオン処理

Credential ProviderでGenericCardLogonを選択すると、カード検出WorkerがServiceへ認証要求を送ります。

Serviceは次の処理を行います。

```text
AUTH要求
  ↓
RC-S380 / FeliCa Polling
  ↓
8-byte IDm
  ↓
SHA-256("FeliCa:" + IDm)
  ↓
cards.json照合
  ↓
DPAPI復号
  ↓
ユーザー名 + パスワード
  ↓
Credential Provider
  ↓
Windows標準の資格情報serialization
```

Credential Provider側では、カード認証のためのReader待機をLogonUIのUIスレッドで直接行わないようにしています。カード検出はバックグラウンドWorkerで行い、通常のPassword/PIN Providerを利用できる状態を維持します。

## セキュリティ上の注意

### LSA Authentication Packageを使用しない

本プロジェクトでは、WindowsのLSA `Authentication Packages` を変更しません。

過去の実験版とは異なり、独自のLSA Authentication Packageを登録する方式は採用していません。

### IDmについて

FeliCa IDmは秘密鍵ではありません。IDm自体をパスワードや秘密情報として扱う設計にはしていません。

登録DBには以下だけを保存します。

```text
SHA-256("FeliCa:" + IDm)
```

### Windowsパスワードについて

登録時に入力されたWindowsパスワードはDPAPI `DataProtectionScope.LocalMachine` で保護して保存されます。

そのため、`cards.json` は公開してよいファイルではありません。バックアップする場合も安全な場所に保管してください。

### Named Pipe

ServiceはLocalSystemで動作し、Named PipeにはLocalSystemとAdministratorsを対象としたACLを設定しています。

また、Service側ではNamed Pipeのクライアントプロセスを確認し、想定外のIPCクライアントからの認証要求を拒否します。

## ビルド環境

### 必要なもの

- Windows 11 x64
- Visual Studio 2022
- .NET Framework 4.8 Developer Pack
- C++ Desktop Development
- Windows SDK
- x64 build tools
- Sony NFC Port Software / FeliCa Library

Credential ProviderはC++、Manager / Service / Coreは.NET Framework 4.8系です。

## ビルド

1. `GenericCardLogon-RC-S380.sln` をVisual Studioで開く
2. 構成を `Release`
3. プラットフォームを `x64`
4. `Build > Build Solution`

主要プロジェクト:

```text
src/
├─ GenericCardLogon.Core/
├─ GenericCardLogon.Manager/
├─ GenericCardLogon.Service/
├─ GenericCardLogon.CredentialProvider/
└─ GenericCardLogon.Installer/
```

## インストール

管理者権限のPowerShellから、ビルド後に以下を実行できます。

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Install-All.ps1
```

このスクリプトはServiceの登録・起動とCredential Provider DLLのSystem32への配置・登録を行います。

個別に実行する場合:

```powershell
.\scripts\Install-Service.ps1
.\scripts\Install-CredentialProvider.ps1
```

### Installer

`GenericCardLogon.Installer` にはWPFベースのインストーラーも含まれています。

インストーラーは、Service、Credential Provider、LogonUI設定、ショートカット等をまとめて扱う構成です。

## アンインストール / 復旧

```powershell
.\scripts\Uninstall-All.ps1
```

Credential Providerだけを解除する場合:

```powershell
.\scripts\Uninstall-CredentialProvider.ps1
```

Serviceだけを解除する場合:

```powershell
.\scripts\Uninstall-Service.ps1
```

**Windowsログオン画面で問題が発生した場合でも、通常のPassword/PINログオンを残しておくことを強く推奨します。**

## 設定

LogonUI表示文字列は以下のレジストリを使用します。

```text
HKLM\SOFTWARE\GenericCardLogon
```

主な設定:

```text
ProviderLabel
Instruction
```

既定値:

```text
ProviderLabel = GenericCardLogon
Instruction  = ICカードをかざしてください
```

Managerから変更できます。

## ログ / トラブルシューティング

### Credential Providerの登録確認

```powershell
.\scripts\Check-CredentialProvider.ps1
```

### インストール状態確認

```powershell
.\scripts\Check-Installation.ps1
```

### RC-S380が検出されない

1. Sony NFC Port Softwareがインストールされているか確認
2. RC-S380をUSBから抜き差し
3. Windowsのデバイス認識を確認
4. Sony FeliCa Libraryの `felica.dll` が存在するか確認
5. Managerを再起動

通常のFeliCaとApple Pay ExpressではRF応答特性が異なるため、同じ条件でも検出率が異なる場合があります。

## Apple Watchについて

Apple WatchのExpress Suicaでは、カード検出時にApple Watch側の画面遷移が発生します。本版ではExpress IDm取得成功後にそのAUTH要求のPollingを継続しないようにしています。

ただし、**Apple Watchの表示上の現象をこのアプリが原因と断定するものではありません**。Reader、Watch、周辺環境の影響も考えられるため、問題がある場合はGCLを停止した状態でも比較してください。

## 制限事項

- RC-S380以外のReaderは正式な対象としていません
- FeliCa IDmを秘密鍵として利用するものではありません
- WebAuthn / FIDO2 / Passkey Providerではありません
- Windows Hello PIN UIを置き換えるものではありません
- LSA Authentication Packageは使用しません
- Apple Pay / Express ModeのWindows側設定を変更するものではありません
- Readerやカード、OS、Sony NFC Port Softwareの組み合わせによって検出結果が変わる可能性があります

## ディレクトリ構成

```text
GenericCardLogon-RC-S380/
├─ src/
│  ├─ GenericCardLogon.Core/
│  ├─ GenericCardLogon.Manager/
│  ├─ GenericCardLogon.Service/
│  ├─ GenericCardLogon.CredentialProvider/
│  └─ GenericCardLogon.Installer/
├─ scripts/
├─ README.md
├─ README-v9.5.md
├─ APPLE_PAY_EXPRESS_NOTES.md
└─ GenericCardLogon-RC-S380.sln
```

## 開発方針

- Windows標準のPassword/PINログオンを壊さない
- LSA Authentication Packageを変更しない
- ReaderアクセスをServiceに分離する
- Credential ProviderのUIスレッドをブロックしない
- IDmを平文保存しない
- 未公開Sony APIや未検証のReaderコマンドを不用意に追加しない
- Apple Pay Expressと通常FeliCaを別経路として扱い、失敗時に安全にフォールバックする

## ライセンス
このソフトウェアは [MIT License](LICENSE) のもとで公開されています。
## Disclaimer

本ソフトウェアは現状のWindows、Sony NFC Port Software、RC-S380、FeliCaカード環境を対象とした実験・開発用ソフトウェアです。

Windowsログオンに関わるため、実機導入前にバックアップと復旧手段を確保してください。作者は、設定変更・Credential Provider登録・カード登録・Windows更新・Reader/カード相性などによって発生したログオン不能、データ損失、その他の損害について保証しません。

## GitHub Actions / Release

GitHub Actions can build the complete x64 installer automatically and attach it to a GitHub Release.

A release is created when a semantic-version tag such as `v9.5.0` is pushed:

```powershell
git tag v9.5.0
git push origin v9.5.0
```

The workflow is located at `.github/workflows/release.yml` and builds `GCL-Installer.exe` with MSBuild on a Windows GitHub-hosted runner. The resulting installer is uploaded to the release as `GCL-Installer-v9.5.0.exe`.

The workflow also prints and verifies a SHA-256 hash for the generated installer.

### Manual build

The same installer can be built locally with:

```powershell
.\Build-GCL-Installer.ps1
```

Output:

```text
src\GenericCardLogon.Installer\bin\x64\Release\net48\GCL-Installer.exe
```

The release workflow intentionally builds the main GenericCardLogon solution first and the installer second because the installer embeds the Service, Manager, Core, PC/SC and Credential Provider binaries.
