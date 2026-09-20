# v9.2 build fixes

This package fixes the compile errors visible in the v9.1 build attempt.

- Credential Provider C++ source is UTF-8; the MSVC project now passes `/utf-8` in Debug and Release.
  This prevents Japanese string literals from being parsed with the system code page and causing the large cascade of C2001/C2146/C2601 errors.
- `CardRegistration.PasswordProtectedBase64` was added to Core because the Service consumes this field.
- Manager registration now asks for the Windows password, verifies it with `LogonUser`, and stores a DPAPI LocalMachine-protected copy for the LocalSystem service.
- Manager clears the password box after registration and keeps the Register button state synchronized with both fields.
- Custom LSA Authentication Package remains removed. Installation scripts do not modify `HKLM\SYSTEM\CurrentControlSet\Control\Lsa\Authentication Packages`.

Build target: Visual Studio 2022, Release | x64.

Do not install this package until the solution builds successfully.
