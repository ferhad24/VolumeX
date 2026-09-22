# Runs elevated: replaces the running Crescendo with the staged build and
# installs the engine on the default playback device. One UAC prompt covers it.
$ErrorActionPreference = 'Continue'
$log = "$env:ProgramData\Crescendo\elevated-install.log"
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
"$(Get-Date -Format u)  start" | Out-File $log -Encoding utf8

Get-Process Crescendo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Copy-Item "D:\Crescendo\artifacts\release\*" "D:\Crescendo\dist\" -Force
"copied release -> dist" | Out-File $log -Append -Encoding utf8

# Already elevated, so the child inherits the token: no second prompt.
Start-Process "D:\Crescendo\dist\Crescendo.exe" -ArgumentList "--install-engine" -WorkingDirectory "D:\Crescendo\dist"
"launched --install-engine" | Out-File $log -Append -Encoding utf8
