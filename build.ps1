# Builds Claude Assist into .\app and (re)starts it.
$ErrorActionPreference = 'Stop'
Get-Process ClaudeAssist -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$PSScriptRoot\ClaudeAssist.csproj" -c Release -o "$PSScriptRoot\app" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Start-Process "$PSScriptRoot\app\ClaudeAssist.exe"
Write-Host "Claude Assist is running in the tray. Press Ctrl+Alt+A."
