# PowerShell 7 or Windows PowerShell 5.1, from repository root.
# Runs every test suite, then publishes into a NEW versioned folder (default dist/MixDock-win-x64-v<version>).
# It refuses to write into a folder that already has files, so a running or previous copy is never overwritten:
#   .\scripts\build.ps1 [-Out dist/MixDock-win-x64-v1.3.1]
param([string]$Out = '')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content src/MixDock/MixDock.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $Out) { $Out = "dist/MixDock-win-x64-v$version" }
if ((Test-Path $Out) -and (Get-ChildItem $Out -Force | Select-Object -First 1)) { throw "$Out already has files; choose a new -Out folder (nothing is overwritten)." }
foreach ($t in 'Dsp', 'Gamepad', 'Waveform', 'Pad', 'Sync', 'Routing', 'AudioTiming', 'Remote') {
    Write-Host "Running $t tests..."
    dotnet run --project "tests/MixDock.${t}Tests/MixDock.${t}Tests.csproj" --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "$t tests failed" }
}
Write-Host "Publishing portable win-x64 application $version ..."
dotnet publish src/MixDock/MixDock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $Out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$exe = Join-Path $Out 'MixDock.exe'
$evidence = [IO.Path]::GetFullPath("out\release-v$version-ui")
$check = Start-Process -FilePath ([IO.Path]::GetFullPath($exe)) -ArgumentList '--ui-check', ('"' + $evidence + '"') -WindowStyle Hidden -PassThru
if (!$check.WaitForExit(90000)) { Stop-Process -Id $check.Id; throw 'Offscreen UI check timed out' }
if ($check.ExitCode -ne 0) { throw "Offscreen UI check failed ($($check.ExitCode)); see $evidence" }
$result = Get-Content (Join-Path $evidence 'ui-check.json') -Raw | ConvertFrom-Json
if ($result.failures -ne 0) { throw 'Offscreen interaction assertions failed' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-images.ps1 -Evidence $evidence
if ($LASTEXITCODE -ne 0) { throw 'Runtime image verification failed' }
Copy-Item TEST_ME.md -Destination $Out
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "Done: $exe  version $((Get-Item $exe).VersionInfo.FileVersion)  sha256 $hash"

