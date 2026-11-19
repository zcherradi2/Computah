# Builds Computah into .\app and (re)starts it.
$ErrorActionPreference = 'Stop'
Get-Process Computah -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$PSScriptRoot\Computah.csproj" -c Release -o "$PSScriptRoot\app" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Start-Process "$PSScriptRoot\app\Computah.exe"
Write-Host "Computah is running in the tray. Alt+A: new session, Ctrl+Alt+A: continue."
