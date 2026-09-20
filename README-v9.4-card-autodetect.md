# GenericCardLogon v9.4 - card-triggered Credential Provider

This build changes the Credential Provider behavior so that selecting the GenericCardLogon tile immediately asks the LocalSystem service to wait for a FeliCa card.

Flow:

1. Select GenericCardLogon on the Windows sign-in screen.
2. Credential Provider calls the restricted named pipe.
3. LocalSystem service polls RC-S380 for FeliCa (system code 0x0003, then 0xFFFF).
4. When a registered card is found, the Credential Provider receives the protected Windows password from the service.
5. The provider returns standard Negotiate/Kerberos interactive logon serialization with auto-logon enabled.

The existing Enter-key path remains as a fallback if no card is detected during selection.

Important:
- Do not modify LSA Authentication Packages.
- Standard Windows password/PIN providers remain installed.
- Rebuild the Credential Provider Release|x64 before installing it.
- The service does not need to be rebuilt for this change.
