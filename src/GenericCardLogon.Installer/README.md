# GCL-Installer v9.5

Modern Japanese WPF installer for GenericCardLogon.

Build requirements: Visual Studio with .NET Framework 4.8 developer pack and x64 build support.

Build `GCL-Installer.csproj` as Release/x64. The embedded payload is taken from the existing v9.4 Release build outputs in `bin/AnyCPU/Release/net48` and `bin/x64/Release`.

The installer:
- installs the LocalSystem service
- installs/registers the x64 Credential Provider
- creates desktop and Start Menu shortcuts for Manager and Installer
- creates default LogonUI strings in HKLM\SOFTWARE\GenericCardLogon
- never modifies LSA Authentication Packages


Embedded payload resources use the exact LogicalName `Payload.<filename>`; runtime extraction must use the same manifest name.
