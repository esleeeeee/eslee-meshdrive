param([string]$JavaHome = $env:JAVA_HOME, [string]$AndroidSdk = $env:ANDROID_HOME, [switch]$Release)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath "$JavaHome/bin/java.exe")) { throw 'JDK 21 path is required (-JavaHome)' }
if (!(Test-Path -LiteralPath "$AndroidSdk/platforms")) { throw 'Android SDK path is required (-AndroidSdk)' }
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskBuild = Join-Path ([IO.Path]::GetTempPath()) ('meshdrive-android-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskBuild | Out-Null
# Gradle test workers can fail class lookup in non-ASCII Windows checkout paths.
robocopy "$taskRoot/android" $taskBuild /E /XD .gradle .kotlin build /XF local.properties /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Android source staging failed' }
$env:JAVA_HOME = $JavaHome
$env:ANDROID_HOME = $AndroidSdk
$taskVariant = if ($Release) { 'release' } else { 'debug' }
$taskTasks = if ($Release) { @(':app:assembleRelease', ':app:testDebugUnitTest', ':app:lintRelease', ':app:lintDebug') } else { @(':app:assembleDebug', ':app:testDebugUnitTest', ':app:lintDebug') }
if ($Release) {
    $taskSigning = Join-Path $env:LOCALAPPDATA 'eslee/MeshDriveBuild/signing'
    $env:MESHDRIVE_KEYSTORE = Join-Path $taskSigning 'meshdrive.p12'
    if (!(Test-Path -LiteralPath $env:MESHDRIVE_KEYSTORE)) { throw 'Run Initialize-AndroidSigning.ps1 first.' }
    $taskSecure = Get-Content -LiteralPath (Join-Path $taskSigning 'password.dpapi') | ConvertTo-SecureString
    $env:MESHDRIVE_STORE_PASSWORD = [Net.NetworkCredential]::new('', $taskSecure).Password
}
try { & "$taskBuild/gradlew.bat" -p $taskBuild @taskTasks --no-daemon --console=plain }
finally { if ($Release) { Remove-Item Env:MESHDRIVE_STORE_PASSWORD; Remove-Item Env:MESHDRIVE_KEYSTORE } }
if ($LASTEXITCODE -ne 0) { throw "Android verification failed; inspect $taskBuild/app/build/reports" }
New-Item -ItemType Directory -Force "$taskRoot/artifacts/android" | Out-Null
Copy-Item "$taskBuild/app/build/outputs/apk/$taskVariant/app-$taskVariant.apk" "$taskRoot/artifacts/android/MeshDrive-$taskVariant.apk"
$taskReports = "$taskRoot/artifacts/android/reports/$taskVariant"
New-Item -ItemType Directory -Force $taskReports | Out-Null
Copy-Item "$taskBuild/app/build/reports/*" $taskReports -Recurse -Force
Copy-Item "$taskBuild/app/build/test-results/testDebugUnitTest" "$taskReports/test-results" -Recurse -Force
Get-FileHash "$taskRoot/artifacts/android/MeshDrive-$taskVariant.apk" -Algorithm SHA256
