@echo off
setlocal
powershell.exe -NoProfile -Command "$p = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq 'Native Bot Create' } | Select-Object -First 1; if (-not $p) { Write-Error 'Native Bot Create не найден среди установленных приложений.'; exit 1 }; Start-Process msiexec.exe -ArgumentList @('/x', $p.PSChildName)"
if errorlevel 1 pause
