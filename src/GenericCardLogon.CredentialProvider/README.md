# GenericCardLogon Credential Provider

Windows Logon UI に `カードをかざしてください` を表示するための x64 Credential Provider 試作です。

## 現在の範囲

- Windows Logon / Unlock Workstation / Credential UI に対応する使用シナリオを受け付ける
- Credential tile に `GenericCardLogon` と `カードをかざしてください` を表示
- 認証処理（IDm取得、cards.json照合、Windowsログオン用serialization）はまだ接続していない

## 注意

Credential Provider は Windows のログオン処理に関係するシステムコンポーネントです。
テストPCでは必ず通常のパスワード/PIN等の既存ログオン手段を残してください。

`DllRegisterServer` は HKLM に Credential Provider として登録します。管理者権限が必要です。
登録前に通常のログオン手段と復旧方法を確認してください。

本プロジェクトは Microsoft の Credential Provider API の構造に沿って実装しています。
`GetStringValue` が Logon UI に CPFT_LARGE_TEXT の文字列を返す構成です。


### ビルド修正
Credential ProviderはWindows SDKのCREDENTIAL_PROVIDER_FIELD_DESCRIPTOR定義に合わせ、
フィールド順序とSTATUS_ICON列挙値を修正しています。またWindows SDK側のDllGetClassObject/DllCanUnloadNow宣言との
リンケージ競合を避けるため、エクスポート関数のプロトタイプをヘッダーから外しています。
