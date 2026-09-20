# GCL-Installer build

The installer project is intentionally kept out of `GenericCardLogon-RC-S380.sln`.
It embeds the already-built Credential Provider DLL, so building both projects in parallel can cause CS1566/file-lock errors.

Use `Build-GCL-Installer.ps1` from the repository root. It builds the main solution first and the installer second.

Output:
`src\GenericCardLogon.Installer\bin\x64\Release\GCL-Installer.exe`
