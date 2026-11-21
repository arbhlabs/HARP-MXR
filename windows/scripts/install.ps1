param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MixDock-1.3.3'), [switch]$NoShortcuts, [switch]$NoRegistry, [switch]$Quiet)
$ErrorActionPreference='Stop'
function GetPayloadHash([string]$Path){
 $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
 try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
}
$target=[IO.Path]::GetFullPath($InstallRoot)
if((Split-Path $target -Leaf) -ne 'MixDock-1.3.3'){throw 'Install folder must be named MixDock-1.3.3.'}
$exe=Join-Path $target 'MixDock.exe'
if(Get-Process MixDock -ErrorAction SilentlyContinue | Where-Object {try{$_.Path -eq $exe}catch{$false}}){throw 'Close this version of MixDock before installing.'}
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach($file in 'MixDock.exe','TEST_ME.md','uninstall.ps1'){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $target $file) -Force}
if(!$NoShortcuts){
 $shell=New-Object -ComObject WScript.Shell
 foreach($dir in @([Environment]::GetFolderPath('Desktop'),(Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))){
  $link=$shell.CreateShortcut((Join-Path $dir 'MixDock.lnk'));$link.TargetPath=$exe;$link.WorkingDirectory=$target;$link.IconLocation="$exe,0";$link.Save()
 }
}
if(!$NoRegistry){
 $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MixDock-1.3.3'
 New-Item -Path $key -Force | Out-Null
 $values=@{DisplayName='MixDock 1.3.3';DisplayVersion='1.3.3';Publisher='ARBH Labs';InstallLocation=$target;DisplayIcon=$exe;UninstallString=('powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $target 'uninstall.ps1')+'"')}
 foreach($name in $values.Keys){Set-ItemProperty -Path $key -Name $name -Value $values[$name]}
 Set-ItemProperty -Path $key -Name NoModify -Type DWord -Value 1
 Set-ItemProperty -Path $key -Name NoRepair -Type DWord -Value 1
}
Set-Content -LiteralPath (Join-Path $target 'installed.sha256') -Value (GetPayloadHash $exe)
if(!$Quiet){$shell=New-Object -ComObject WScript.Shell;$null=$shell.Popup('MixDock is installed. Open the desktop or Start menu shortcut when ready. Read TEST_ME.md first. Audio and settings have not been changed.',0,'MixDock 1.3.3',64)}
