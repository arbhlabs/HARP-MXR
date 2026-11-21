param([switch]$NoShortcuts,[switch]$NoRegistry,[switch]$Quiet)
$ErrorActionPreference='Stop'
$target=[IO.Path]::GetFullPath($PSScriptRoot)
if((Split-Path $target -Leaf) -ne 'MixDock-1.3.3'){throw 'Unexpected uninstall location; nothing removed.'}
$exe=Join-Path $target 'MixDock.exe'
if(Get-Process MixDock -ErrorAction SilentlyContinue | Where-Object {try{$_.Path -eq $exe}catch{$false}}){throw 'Close MixDock before uninstalling.'}
if(!$NoShortcuts){
 $shell=New-Object -ComObject WScript.Shell
 foreach($dir in @([Environment]::GetFolderPath('Desktop'),(Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))){
  $path=Join-Path $dir 'MixDock.lnk'
  if((Test-Path -LiteralPath $path) -and $shell.CreateShortcut($path).TargetPath -eq $exe){Remove-Item -LiteralPath $path}
 }
}
if(!$NoRegistry){Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MixDock-1.3.3' -Recurse -ErrorAction SilentlyContinue}
# Only this explicitly checked version folder is removed. User settings, tracks and older releases remain.
Remove-Item -LiteralPath $target -Recurse -Force
