# GenericCardLogon v9.5

Changes:
- Card detection in the Credential Provider now runs asynchronously after tile selection and notifies LogonUI with `CredentialsChanged`.
- The LogonUI instruction message is configurable from Manager.
- UI settings are stored in HKLM\SOFTWARE\GenericCardLogon and are readable by LogonUI.
- Manager and Installer shortcuts are created by the new WPF installer.
- Japanese modern installer UI.
- LSA Authentication Packages are never modified.

## Build
Run `Build-GCL-Installer.ps1` in an elevated or normal developer PowerShell after Visual Studio is installed. The script builds the main solution first, then the installer.

The final installer is: `src\GenericCardLogon.Installer\bin\x64\Release\GCL-Installer.exe`.
