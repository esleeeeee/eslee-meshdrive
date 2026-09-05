param([string]$JavaHome = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath "$JavaHome/bin/keytool.exe")) { throw 'JDK path required' }
$taskSigning = Join-Path $env:LOCALAPPDATA 'eslee/MeshDriveBuild/signing'
New-Item -ItemType Directory -Force -Path $taskSigning | Out-Null
$taskKey = Join-Path $taskSigning 'meshdrive.p12'
$taskPassword = Join-Path $taskSigning 'password.dpapi'
if ((Test-Path -LiteralPath $taskKey) -or (Test-Path -LiteralPath $taskPassword)) {
    if (!(Test-Path -LiteralPath $taskKey) -or !(Test-Path -LiteralPath $taskPassword)) { throw 'Signing state incomplete; preserve existing files and recover manually.' }
    Write-Output "Existing signing identity preserved: $taskSigning"
    return
}
# Generated credentials are stored outside the checkout, encrypted for this Windows user.
$taskSecret = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$taskSecure = ConvertTo-SecureString $taskSecret -AsPlainText -Force
$taskSecure | ConvertFrom-SecureString | Set-Content -LiteralPath $taskPassword
$env:MESHDRIVE_STORE_PASSWORD = $taskSecret
try {
    & "$JavaHome/bin/keytool.exe" -genkeypair -keystore $taskKey -storetype PKCS12 -alias meshdrive -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=eslee MeshDrive, O=eslee' -storepass:env MESHDRIVE_STORE_PASSWORD -keypass:env MESHDRIVE_STORE_PASSWORD
    if ($LASTEXITCODE -ne 0) { throw 'Signing key generation failed; preserve files for recovery.' }
} finally { Remove-Item Env:MESHDRIVE_STORE_PASSWORD; $taskSecret = $null }
Write-Output "Signing identity created outside repository: $taskSigning"
