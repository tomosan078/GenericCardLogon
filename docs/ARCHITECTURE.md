# Architecture

## Components

### GenericCardLogon.Core

カード登録情報とIDm hash処理を共有する.NET Framework 4.8ライブラリです。

- `RegistrationStore`
- `CardHash`
- registration models

Default DB:

```text
%ProgramData%\GenericCardLogon\cards.json
```

### GenericCardLogon.Manager

カード登録、登録削除、LogonUI表示設定、Reader状態確認などを行うWPFアプリです。

登録時にはWindowsパスワードを検証し、DPAPI LocalMachineで保護してからDBへ保存します。

### GenericCardLogon.Service

LocalSystemで動作するWindows Serviceです。

Credential ProviderからNamed Pipe `GenericCardLogon.Logon.v1` 経由で `AUTH` 要求を受け、RC-S380/FeliCa Libraryを使ってカードをPollingします。

カードのIDm hashが登録DBと一致した場合、DPAPIで保護された資格情報を復号してCredential Providerへ返します。

### GenericCardLogon.CredentialProvider

x64 Windows Credential Providerです。

カード検出処理はバックグラウンドWorkerからServiceへ要求し、検出成功後にCredential Providerへ資格情報を渡します。

`SetSelected` / `SetDeselected` / `CredentialsChanged` の状態管理により、通常のWindows Password/PIN Providerとの共存を維持します。

### GenericCardLogon.Installer

Service、Credential Provider、LogonUI設定、ショートカット等をまとめて導入するWPFインストーラーです。

## Authentication flow

```text
LogonUI
  │
  │ Select GenericCardLogon
  ▼
Credential Provider
  │
  │ AUTH
  ▼
Named Pipe
  │
  ▼
LocalSystem Service
  │
  ├─ Sony FeliCa Library
  │       │
  │       └─ RC-S380
  │
  ├─ FeliCa IDm
  │
  ├─ SHA-256("FeliCa:" + IDm)
  │
  └─ cards.json lookup
  │
  └─ DPAPI decrypt
  ▼
Credential Provider
  │
  └─ Windows credential serialization
  ▼
Windows authentication
```

## Trust boundaries

- ReaderアクセスはServiceに集中
- Credential ProviderとService間はACL付きNamed Pipe
- 登録DBにはIDmそのものを保存しない
- パスワードはDPAPI保護
- LSA Authentication Packageは変更しない

## Express handling

Express / Apple Payは通常FeliCaと同一のPolling処理ではなく、`0x0003`を対象とした専用経路を持ちます。

IDm取得成功後は、そのAUTH要求について追加Pollingを行わず、カード処理を終了します。
