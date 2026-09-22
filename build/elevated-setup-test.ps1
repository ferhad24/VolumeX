# Runs elevated: closes the dev copy and installs dist\Crescendo-Setup.exe
# silently, exactly as the in-app updater would.
$log = "$env:ProgramData\Crescendo\setup-test.log"
"$(Get-Date -Format u)  start" | Out-File $log -Encoding utf8
Get-Process Crescendo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process "D:\Crescendo\dist\Crescendo-Setup.exe" -ArgumentList "/SILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$env:ProgramData\Crescendo\inno.log`"" -Wait -PassThru
"setup exit code: $($p.ExitCode)" | Out-File $log -Append -Encoding utf8
