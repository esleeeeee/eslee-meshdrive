param([string]$InnoCompiler = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    dotnet build MeshDrive.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test MeshDrive.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    dotnet publish src/MeshDrive.Windows -c Release -r win-x64 --self-contained true -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }
    dotnet publish src/MeshDrive.Agent -c Release -r win-x64 --self-contained true -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed' }
    if (!(Test-Path -LiteralPath $InnoCompiler)) { throw "Inno Setup compiler not found: $InnoCompiler" }
    & $InnoCompiler installer/MeshDrive.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed' }
    Get-FileHash artifacts/installer/MeshDriveSetup.exe -Algorithm SHA256
} finally { Pop-Location }
