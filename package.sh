#!/bin/sh
# Makes the ready-made download, for both Apple silicon and Intel Macs:
# dist/RAM-Limiter-<version>.zip, holding the app and the extension.
set -e
cd "$(dirname "$0")"
version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" app/Info.plist)
out="dist/RAM Limiter"
app="$out/RAM Limiter.app"
rm -rf dist
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp app/AppIcon.icns "$app/Contents/Resources/"
cp app/Info.plist "$app/Contents/Info.plist"
for arch in arm64 x86_64; do
  swiftc -O -target "$arch-apple-macosx14.0" app/main.swift -o "dist/$arch"
done
lipo -create dist/arm64 dist/x86_64 -output "$app/Contents/MacOS/RAMLimiter"
rm dist/arm64 dist/x86_64
codesign --force --sign - "$app"
cp -R extension LICENSE "$out/"
cp app/INSTALL.txt "$out/How to install.txt"
find "$out" -name .DS_Store -delete
ditto -c -k --norsrc --keepParent "$out" "dist/RAM-Limiter-$version.zip"
echo "Made dist/RAM-Limiter-$version.zip"
