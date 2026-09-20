# Security Policy

## Scope

GenericCardLogonはWindowsログオンに関係するCredential Providerを含むため、通常のデスクトップアプリよりも慎重な取り扱いが必要です。

## Important security properties

- FeliCa IDmは秘密情報として扱わず、DBにはSHA-256 hashのみ保存します。
- 登録されたWindowsパスワードはDPAPI LocalMachineで保護します。
- ServiceはLocalSystemで動作します。
- Credential ProviderとServiceのIPCにはNamed Pipe ACLを使用します。
- LSA Authentication Packageを追加・変更しません。
- WebAuthn / Passkey機能は含みません。

## Reporting a vulnerability

公開リポジトリで利用する場合は、重大なセキュリティ問題をIssueへ直接投稿する前に、メンテナーへ非公開で連絡できる窓口を用意することを推奨します。

現時点では専用のSecurity Advisory窓口を設定していません。

## Recovery

テスト環境では必ず以下を用意してください。

- ローカルまたはMicrosoftアカウントの通常ログオン手段
- 管理者アカウント
- WinREまたは別の管理経路
- Credential Provider登録を解除するためのPowerShellスクリプト

Credential Providerを登録した状態で通常のログオン手段を無効化しないでください。
