FAAM.UI station notes
=====================

Install
-------
Run FAAM.UI-win-Setup.exe from the GitHub release. It installs to
  %LOCALAPPDATA%\FAAM.UI\current
If Windows says the file is not safe (SmartScreen): "More info" > "Run anyway".
The installer is not code-signed yet. Later updates come through
Home > Version > Update... and do not show this warning.

Camera says "no video" but is connected
---------------------------------------
Then the issue is the firewall. Go to the current folder and run:

  PS C:\Users\PC_FBN\AppData\Local\FAAM.UI\current> powershell -ExecutionPolicy Bypass -File .\allow-firewall.ps1

From Command Prompt (cmd) instead of PowerShell:

  powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\FAAM.UI\current\allow-firewall.ps1"

It asks for Administrator. Restart FAAM afterwards.
Once per station is enough - updates keep the same path, so the rule stays valid.
