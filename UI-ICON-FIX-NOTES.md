# UI / Icon Fix

- Manager: replaced WPF default TabItem/Button templates so selected/pressed controls stay dark instead of turning white.
- Manager and GCL-Installer: embedded `GenericCardLogon.ico` via `ApplicationIcon`.
- ICO contains 16/24/32/48/64/128/256 px variants.
- The ICO file is compiled into the EXE and does not need to be shipped beside it.
