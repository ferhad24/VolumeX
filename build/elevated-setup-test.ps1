# Runs elevated: closes any running copy and starts dist\VolumeX-Setup.exe
# silently and detached - the way the in-app updater does - then returns at
# once. Waiting on the setup from here would put the app it relaunches inside
# this script's process tree, which is not what users get.
$log = "$env:ProgramData\Crescendo\setup-test.log"
"$(Get-Date -Format u)  start" | Out-File $log -Encoding utf8
Get-Process VolumeX, Crescendo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-Process "D:\Crescendo\dist\VolumeX-Setup.exe" -ArgumentList "/SILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$env:ProgramData\Crescendo\inno.log`""
"setup started detached" | Out-File $log -Append -Encoding utf8
