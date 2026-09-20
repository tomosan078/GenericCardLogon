# GenericCardLogon

<p align="center">
  <img src="./GenericCardLogon.ico" alt="GenericCardLogon" width="128">
</p>

<p align="center">
  Sony PaSoRi RC-S380 と FeliCa カードを使って Windows 11 にログオン／ロック解除する Credential Provider
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-11%20x64-237AEB" alt="Windows 11 x64">
  <img src="https://img.shields.io/badge/Reader-RC--S380-7086BD" alt="Sony PaSoRi RC-S380">
  <img src="https://img.shields.io/badge/FeliCa-IDm-28B84A" alt="FeliCa IDm">
</p>

GenericCardLogon は、Sony PaSoRi RC-S380 で FeliCa の IDm を検出し、登録済みのカードを認証情報として Windows 11 のローカルアカウントへログオン／ロック解除する Credential Provider です。

認証処理は Windows Credential Provider、LocalSystem サービス、限定された Named Pipe、Sony FeliCa Library を組み合わせて構成しています。Windows 標準のパスワード／PIN Credential Provider は無効化しません。

> [!IMPORTANT]
> Credential Provider と Windows サービスをシステムへ登録します。インストール前に、標準の Windows パスワードまたは PIN でログオンできることと、別のローカル管理者アカウントを利用できることを確認してください。

## 免責事項
本ソフトウェアの利用によって生じた、直接的または間接的な損害、データの消失・破損、システム障害、その他一切の損害について、開発者はその責任を負いません。

本ソフトウェアは、利用者自身の責任において使用するものとし、本ソフトウェアの利用または利用できないことによって生じた損害について、開発者は補償その他の責任を負わないものとします。

また、本ソフトウェアの動作について、完全性、正確性、継続的な動作を保証するものではありません。

## 主な機能

- Sony PaSoRi RC-S380 による FeliCa 検出
- FeliCa IDm によるカード登録・認証
- Suica、通常の FeliCa、Apple Pay Express Suica の検出
- FeliCa IDm の SHA-256 ハッシュによる登録情報管理
- Windows ローカルアカウントへのログオン／ロック解除
- LocalSystem サービスによるカード検出と認証処理
- Named Pipe による Credential Provider とサービス間通信
- Windows 標準パスワード／PIN Credential Provider との共存
- 管理画面からのカード登録・削除
- LogonUI のタイル名・案内メッセージ設定
- インストール、修復、アンインストールに対応したGUI Installer
- デスクトップ／スタートメニューへの管理画面ショートカット

## 動作環境

- Windows 11 x64
- Sony PaSoRi RC-S380
- Sony FeliCa Library
- Windows ローカルアカウント
- .NET Framework 4.8
- Visual Studio 2022 / MSVC C++17（ビルド時）
- Windows 11 SDK（Credential Provider ビルド時）

RC-S380以外のカードリーダーについては動作を保証していません。

WebAuthn、Passkey、FIDO2認証器としてのブラウザ認証は現在の仕様には含まれていません。Windows LSA Authentication Package は使用しません。

## 認証と保護

管理画面では Windows ユーザー名、現在の Windows パスワード、登録するFeliCaカードを指定します。

FeliCa IDmそのものは登録データとして保存せず、次の値をSHA-256でハッシュ化した64文字の16進数を使用します。

```text
SHA-256("FeliCa:" + IDm)
```

Windows パスワードは LocalMachine スコープの DPAPI により暗号化して保存します。

カード認証時は、選択されたWindowsユーザーと登録済みカードの組み合わせを確認し、サービスからCredential Providerへ認証情報を渡します。

標準のWindowsパスワード／PIN Credential Providerは無効化しません。FeliCaが利用できない場合でも、通常のWindowsログオン手段を使用できます。

## カード検出

RC-S380からFeliCaのPollingを行い、まずシステムコード `0x0003` を使用します。

`0x0003` は通常のFeliCaだけでなく、Apple Pay Express Suicaの検出にも使用します。

通常のFeliCaについては必要に応じて `0xFFFF` をフォールバックとして使用します。

Express SuicaのIDmを検出できた場合は、それ以上のPollingを継続せず、カード検出処理を終了します。これにより不要な連続Pollingを避けます。

カード登録時には検出の安定性を確保するため、複数回のPolling結果を使用してカードの存在を確認します。

## インストール

GitHub Releases から `GCL-Installer.exe` をダウンロードして起動します。

Installerには必要なGenericCardLogonのコンポーネントが含まれており、GUIからインストール、修復、アンインストールを実行できます。

インストール後の主な配置先は次のとおりです。

| コンポーネント | 配置先 |
| --- | --- |
| Service、Core、Manager | `%ProgramFiles%\GenericCardLogon` |
| Credential Provider | `%SystemRoot%\System32` |
| 登録データ | `%ProgramData%\GenericCardLogon` |
| カード登録データ | `%ProgramData%\GenericCardLogon\cards.json` |

インストール後、スタートメニューまたはデスクトップの `GenericCardLogon Manager` を起動して初期設定を行います。

## 初期設定

1. `GenericCardLogon Manager` を管理者として起動します。
2. 「全体設定」でRC-S380の状態を確認します。
3. 「カード登録」を開きます。
4. Windowsユーザー名と現在のWindowsパスワードを入力します。
5. RC-S380へFeliCaカードをかざします。
6. 検出されたカードを登録します。
7. 必要に応じて「LogonUI設定」で表示名と案内メッセージを変更します。
8. Windowsをロックして、カードによるログオンを確認します。

標準のWindowsパスワード／PINログオンも引き続き使用できます。

## 管理画面

| タブ | 内容 |
| --- | --- |
| 全体設定 | Reader状態、カード検出、基本設定 |
| カード登録 | WindowsアカウントへのFeliCa登録・削除 |
| LogonUI設定 | タイル名、案内メッセージ |
| バージョン情報 | バージョン、プロジェクト情報 |

初期値は次のとおりです。

| 設定 | 初期値 |
| --- | --- |
| タイル名 | `GenericCardLogon` |
| 案内メッセージ | `ICカードをかざしてください` |

LogonUI設定は `HKLM\SOFTWARE\GenericCardLogon` に保存され、ManagerのLogonUI設定からLogonUI.exeにて表示される文言を変更することができます。いわゆるホワイトレーベルです(多分違う)。この設定は、Managerを管理者権限で起動しない限り、変更できません。

## ログオン処理

Windows LogonUIでGenericCardLogonを選択すると、Credential ProviderがLocalSystemサービスへカード検出を要求します。

処理の流れは次のとおりです。

```text
RC-S380
   ↓
Sony FeliCa Library
   ↓
GenericCardLogon Service
   ↓
限定Named Pipe
   ↓
Credential Provider
   ↓
Windows標準Credential Serialization
   ↓
Windows LogonUI
```

カードのIDmはサービス側でハッシュ化して登録情報と照合します。

登録済みカードとして認証できた場合のみ、DPAPIで保護されたWindowsパスワードを使用して標準のWindows Credential Serializationを生成します。

LSA Authentication Packageを追加・変更する方式ではありません。

## セキュリティ

- FeliCa IDmの生値を登録DBへ保存しません。
- 登録値には `SHA-256("FeliCa:" + IDm)` を使用します。
- WindowsパスワードはDPAPI LocalMachineで保護します。
- Credential Providerとサービス間通信には限定されたNamed Pipeを使用します。
- FeliCa IDm自体を秘密鍵として扱いません。
- Windows標準パスワード／PIN Credential Providerを無効化しません。
- LSA `Authentication Packages` を変更しません。

FeliCa IDmはカードを識別するための値であり、秘密情報ではありません。そのため、IDmだけを暗号鍵やパスワードの代わりとして使用する設計にはしていません。

## アンインストールと復旧

Installerの「アンインストール」からGenericCardLogonを削除できます。

通常のアンインストールでは、`%ProgramData%\GenericCardLogon` に保存された登録データを意図せず削除しないようにしています。

再インストール前に登録情報を完全に削除したい場合は、アンインストール後に次のフォルダーを確認してください。

```text
%ProgramData%\GenericCardLogon
```

ログオン画面に問題が発生した場合でも、Windows標準のパスワード／PIN Credential Providerを使用して復旧できる構成を維持することを推奨します。

## ビルド

Visual Studio 2022、Windows 11 SDK、.NET Framework 4.8開発環境が必要です。

リポジトリ直下からVisual Studio Developer PowerShellまたはDeveloper Command Promptを使用してビルドします。

```powershell
msbuild .\GenericCardLogon-RC-S380.sln /p:Configuration=Release /p:Platform=x64
```

Installerを作成する場合は、

```powershell
.\Build-GCL-Installer.ps1
```

を実行します。

C#プロジェクトをビルドする前にNuGet restoreが必要な環境では、次のようにrestoreします。

```powershell
msbuild .\GenericCardLogon-RC-S380.sln /t:Restore /p:Configuration=Release /p:Platform=x64
```

ビルド成果物にはCore、Service、Manager、Credential Providerなどが含まれます。

## プロジェクト構成

| プロジェクト | 役割 |
| --- | --- |
| `GenericCardLogon.Core` | 共通処理、カード情報、認証関連ロジック |
| `GenericCardLogon.Service` | LocalSystemサービス、RC-S380検出、認証処理 |
| `GenericCardLogon.Manager` | カード登録、設定、管理画面 |
| `GenericCardLogon.CredentialProvider` | Windows Credential Provider |
| `GenericCardLogon.Installer` | GUI Installer |
| `GenericCardLogon.WebAuthn` | 現在のリリースでは使用しない実験・基盤コード |

## 制限事項

- 開発者はこのソフトウェアを、Sony PaSoRi RC-S380を利用する想定で開発・動作確認をしています。おそらく他のPaSoRi系リーダライターでも操作すると思われます。
- Windows 11 x64を対象とします。
- Windowsローカルアカウントを対象とします。
- Microsoftアカウント、Entra ID、ドメインアカウントへの対応は保証していません。
- WebAuthn / Passkey / FIDO2によるブラウザ認証は現在の仕様に含まれません。
- LSA Authentication Packageは使用しません。
- FeliCa IDmだけで暗号学的な秘密鍵認証を行う設計ではありません。
- RC-S380以外のReaderでの動作は保証していません。
- カード、Reader、Windows、Sony FeliCa Libraryの組み合わせによって検出結果が変わる場合があります。

## License

ライセンスはリポジトリの `LICENSE` ファイルを参照してください。

Copyright (c) 2026 Pronelt.
