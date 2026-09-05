param([string]$JavaHome = $env:JAVA_HOME, [string]$AndroidSdk = $env:ANDROID_HOME)
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
& "$taskBuild/gradlew.bat" -p $taskBuild :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --console=plain
if ($LASTEXITCODE -ne 0) { throw "Android verification failed; inspect $taskBuild/app/build/reports" }
New-Item -ItemType Directory -Force "$taskRoot/artifacts/android" | Out-Null
Copy-Item "$taskBuild/app/build/outputs/apk/debug/app-debug.apk" "$taskRoot/artifacts/android/MeshDrive-debug.apk"
Copy-Item "$taskBuild/app/build/reports" "$taskRoot/artifacts/android/reports" -Recurse -Force
Get-FileHash "$taskRoot/artifacts/android/MeshDrive-debug.apk" -Algorithm SHA256
