# GenericCardLogon v9.5 — Credential Provider V2

This build converts the native Credential Provider to a Windows Credential Provider V2 implementation.

Changes:
- ICredentialProviderSetUserArray
- ICredentialProviderCredential2 / GetUserSid
- Per-user credentials using the users enumerated by LogonUI
- CPFT_TILE_IMAGE + CPFG_CREDENTIAL_PROVIDER_LOGO
- Embedded 72x72 provider logo generated from GenericCardLogon.ico
- Provider label remains CPFG_CREDENTIAL_PROVIDER_LABEL
- Card/service credentials are checked against the selected Windows user before serialization
- No LSA Authentication Packages changes
- No SessionAgent

Build on Windows/Visual Studio x64. Test in a VM snapshot before installation.
