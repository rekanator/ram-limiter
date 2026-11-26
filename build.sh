#!/bin/sh
# Builds "RAM Limiter.app" next to this script.
set -e
cd "$(dirname "$0")"
app="RAM Limiter.app/Contents"
mkdir -p "$app/MacOS" "$app/Resources"
cp app/AppIcon.icns "$app/Resources/"
cp app/Info.plist "$app/Info.plist"
swiftc -O -target "$(uname -m)-apple-macosx14.0" app/main.swift -o "$app/MacOS/RAMLimiter"
codesign --force --sign - "RAM Limiter.app"
