param([string]$Evidence='out\release-v1.3.2-ui')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
// Windows ClearType has isolated coloured glyph-edge pixels; reject solid 3x3 purple patches and report all fringe candidates separately.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class MixDockPngVerifier {
 public static string Scan(string path) {
  using(var bitmap=new Bitmap(path)) {
   var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
   int stride=data.Stride/4;int[] pixels=new int[stride*bitmap.Height]; Marshal.Copy(data.Scan0,pixels,0,pixels.Length); bitmap.UnlockBits(data);
   int purple=0,solidPurple=0,lo=0,mi=0,hi=0;int top=(int)(115.0/768*bitmap.Height),bottom=(int)(297.0/768*bitmap.Height);
   for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++) {
    int p=pixels[y*stride+x];int r=(p>>16)&255,g=(p>>8)&255,b=p&255;
    if(r>g+35&&b>g+35) { purple++; if(x>0&&y>0&&x<bitmap.Width-1&&y<bitmap.Height-1) { bool solid=true; for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) { int q=pixels[(y+dy)*stride+x+dx],qr=(q>>16)&255,qg=(q>>8)&255,qb=q&255; solid &= qr>qg+35&&qb>qg+35; } if(solid)solidPurple++; } }
    if(y>=top&&y<bottom) {
     if((r==232&&g==146&&b==48)||(r==150&&g==96&&b==36))lo++;
     if((r==78&&g==199&&b==126)||(r==43&&g==119&&b==73))mi++;
     if((r==156&&g==222&&b==236)||(r==98&&g==140&&b==150))hi++;
    }
   }
   if(solidPurple!=0||lo<100||mi<100||hi<100)throw new Exception("Palette/waveform assertion failed: "+path+" solidPurple="+solidPurple+" textFringeCandidates="+purple+" bass="+lo+" mid="+mi+" high="+hi);
   return bitmap.Width+"x"+bitmap.Height+" solidPurple="+solidPurple+" textFringeCandidates="+purple+" bass="+lo+" mid="+mi+" high="+hi;
  }
 }
}
"@
Get-ChildItem (Join-Path $Evidence 'runtime-*.png') | ForEach-Object {[PSCustomObject]@{path=$_.FullName;scan=[MixDockPngVerifier]::Scan($_.FullName)}} | ConvertTo-Json | Set-Content out\png-verification.json
Get-Content out\png-verification.json

