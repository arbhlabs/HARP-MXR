#!/bin/bash
# Builds a universal (Apple silicon + Intel) MixDock.app and a drag-to-Applications MixDock.dmg in dist/.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${VERSION:-1.0.0}"

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/MixDock"

rm -rf dist
APP=dist/MixDock.app
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/MixDock"
chmod +x "$APP/Contents/MacOS/MixDock"
printf "APPL????" > "$APP/Contents/PkgInfo"
sed "s/__VERSION__/$VERSION/g" packaging/Info.plist > "$APP/Contents/Info.plist"

# app icon from the generated 1024 px PNG
if [ -f packaging/icon.png ]; then
  ICONSET=dist/AppIcon.iconset; mkdir -p "$ICONSET"
  for s in 16 32 128 256 512; do
    sips -z $s $s packaging/icon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) packaging/icon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
  rm -rf "$ICONSET"
fi

# ad-hoc signature: required to run on Apple silicon (not notarized: first open needs Open Anyway)
codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP"

mkdir -p dist/dmg
cp -R "$APP" dist/dmg/
ln -s /Applications dist/dmg/Applications
cp packaging/FIRST-OPEN.txt "dist/dmg/First open - read me.txt"
hdiutil create -volname "HARP MXR" -srcfolder dist/dmg -ov -format UDZO dist/MixDock.dmg
rm -rf dist/dmg
echo "Built dist/MixDock.dmg ($VERSION)"
