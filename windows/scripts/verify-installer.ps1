param([string]$Setup='dist/HARP-MXR-Setup-1.5.0.exe',[string]$Portable='dist/MixDock-win-x64-v1.5.0/HARP-MXR.exe')
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$root=[IO.Path]::GetFullPath((Join-Path $PWD ('out/installer-test-'+(Get-Date -Format yyyyMMdd-HHmmss))))
$extract=Join-Path $root 'extracted'
$target=Join-Path $root 'HARP-MXR-1.5.0'
New-Item -ItemType Directory -Path $root | Out-Null
# WinExe must not redirect stdout/stderr (that can return a non-zero exit with empty logs).
function RunHidden([string]$File,[string[]]$Arguments,[int]$Timeout=60000){
 $p=Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
 if(!$p.WaitForExit($Timeout)){Stop-Process -Id $p.Id;throw "Timed out: $File"}
 if($p.ExitCode -ne 0){throw "Failed ($($p.ExitCode)): $File"}
}
RunHidden ([IO.Path]::GetFullPath($Setup)) @('--extract',$extract)
$expected=(Get-FileHash -LiteralPath $Portable -Algorithm SHA256).Hash
if((Get-FileHash -LiteralPath (Join-Path $extract 'HARP-MXR.exe')).Hash -ne $expected){throw 'Extracted payload mismatch'}
RunHidden 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $extract 'install.ps1'),'-InstallRoot',$target,'-NoShortcuts','-NoRegistry','-Quiet')
$installed=(Get-FileHash -LiteralPath (Join-Path $target 'HARP-MXR.exe')).Hash
if($installed -ne $expected){throw 'Installed payload mismatch'}
$evidence=Join-Path $root 'installed-ui'
RunHidden (Join-Path $target 'HARP-MXR.exe') @('--ui-check',$evidence) 90000
$ui=Get-Content (Join-Path $evidence 'ui-check.json') -Raw|ConvertFrom-Json
if($ui.failures -ne 0){throw 'Installed UI check failed'}
# Verify the absolute recursive-removal target is this test's version folder before uninstall.
if([IO.Path]::GetFullPath($target) -ne (Join-Path $root 'HARP-MXR-1.5.0')){throw 'Unsafe uninstall test target'}
RunHidden 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $target 'uninstall.ps1'),'-NoShortcuts','-NoRegistry','-Quiet')
if(Test-Path -LiteralPath $target){throw 'Uninstall left the test folder'}
[ordered]@{setup=[IO.Path]::GetFullPath($Setup);setupSha256=(Get-FileHash $Setup).Hash;payloadSha256=$expected;installedSha256=$installed;extraction='PASS';isolatedInstall='PASS';installedUiChecks=$ui.checks.Count;installedUiFailures=$ui.failures;isolatedUninstall='PASS';shortcutsOrRegistryChanged=$false;installerGuiTested=$false;evidence=$evidence;root=$root}|ConvertTo-Json|Set-Content out/resume-installer-verification.json
Get-Content out/resume-installer-verification.json
