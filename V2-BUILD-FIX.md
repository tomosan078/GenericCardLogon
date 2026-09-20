# Credential Provider V2 build fix

## 修正内容

Visual Studio で次のシンボルが未定義になる環境に対応しました。

- `CPFG_CREDENTIAL_PROVIDER_LOGO`
- `FIELD_PROVIDER_LOGO`
- `PKEY_Identity_QualifiedUserName`
- `PKEY_Identity_UserName`

`CPFG_CREDENTIAL_PROVIDER_LOGO` は Windows 8 以降の Credential Provider V2 契約で使われる GUID、
`PKEY_Identity_*` は `ICredentialProviderUser::GetStringValue` に渡す PROPERTYKEY です。
SDK のヘッダー公開状態に依存しないよう、値をソース内で明示しています。

`CPFG_CREDENTIAL_PROVIDER_LOGO` の GUID:
`2D837775-F6CD-464E-A745-482FD0B47493`

`PKEY_Identity_QualifiedUserName`:
`DA520E51-F4E9-4739-AC82-02E0A95C9030`, PID 100

`PKEY_Identity_UserName`:
`C4322503-78CA-49C6-9ACC-A68E2AFD7B6B`, PID 100

LSA Authentication Packages は変更していません。

## 追加

`SECURITY_WIN32` の二重定義による C4005 警告も除去しました。


## V2 Password Provider compatibility fix
- GCL no longer advertises itself as the default/auto-logon credential.
- Switching from GCL to Windows Password/PIN immediately clears cached card credentials.
- GCL card worker is no longer joined synchronously from SetDeselected, avoiding LogonUI blocking while the LocalSystem service is polling the reader.
- Card results are ignored after the GCL tile is deselected.
