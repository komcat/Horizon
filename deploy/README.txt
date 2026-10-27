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
Then the issue is the firewall. Run allow-firewall.ps1 by its full path
(do not "cd" into the install folder - see below).

From PowerShell:

  powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\FAAM.UI\current\allow-firewall.ps1"

From Command Prompt (cmd):

  powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\FAAM.UI\current\allow-firewall.ps1"

It asks for Administrator. Restart FAAM afterwards.
Once per station is enough - updates keep the same path, so the rule stays valid.

Update fails / FAAM restarts on the old version
-----------------------------------------------
The update swaps the whole "current" folder, which Windows refuses while any
window (cmd, PowerShell, Explorer) is open inside it. Close those windows (or
"cd \" out of the folder), then Home > Version > Update... again - the
download is kept, so it applies straight away.
Log: %LOCALAPPDATA%\velopack\velopack_FAAM.UI.log
