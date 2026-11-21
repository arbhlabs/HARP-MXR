param([string]$Portable = '', [string]$Out = '')
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$version=([xml](Get-Content src\MixDock\MixDock.csproj)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1
if(!$Portable){$Portable="dist\MixDock-win-x64-v$version"}
if(!$Out){$Out="dist\MixDock-Setup-$version.exe"}
if(Test-Path -LiteralPath $Out){throw 'Installer output already exists; choose a new output.'}
$exe=(Resolve-Path (Join-Path $Portable 'MixDock.exe')).Path
$hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$work=Join-Path (Get-Location) ('out\installer-'+(Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $work 'MixDock.exe')
Copy-Item -LiteralPath 'TEST_ME.md','scripts\uninstall.ps1' -Destination $work
$install=Get-Content scripts\install.ps1 -Raw
$install=$install.Replace("`$target=[IO.Path]::GetFullPath(`$InstallRoot)","if((GetPayloadHash (Join-Path `$PSScriptRoot 'MixDock.exe')) -ne '$hash'){throw 'Installer payload integrity check failed.'}`r`n`$target=[IO.Path]::GetFullPath(`$InstallRoot)")
[IO.File]::WriteAllText((Join-Path $work 'install.ps1'),$install,[Text.UTF8Encoding]::new($true))
$target=[IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
$zip=Join-Path $work 'payload.zip'
Compress-Archive -LiteralPath (Join-Path $work 'MixDock.exe'),(Join-Path $work 'install.ps1'),(Join-Path $work 'uninstall.ps1'),(Join-Path $work 'TEST_ME.md') -DestinationPath $zip -CompressionLevel Optimal
$hashPath=Join-Path $work 'payload.sha256'
Set-Content -LiteralPath $hashPath -Value $hash
$publish=Join-Path $work 'setup-bin'
dotnet publish installer\MixDock.Setup\MixDock.Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:PayloadZip=$zip" "-p:PayloadHash=$hashPath" -o $publish
if($LASTEXITCODE -ne 0){throw 'Setup bootstrapper build failed.'}
Copy-Item -LiteralPath (Join-Path $publish "MixDock-Setup-$version.exe") -Destination $target
$installerHash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$manifest=[ordered]@{version=$version;installer=$target;installerSha256=$installerHash;payload=$exe;payloadSha256=$hash;work=$work;unsigned=$true}
$manifest | ConvertTo-Json | Set-Content (Join-Path (Split-Path $target) "installer-$version.json")
$manifest | ConvertTo-Json

