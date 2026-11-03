#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
swift build -c release --package-path "$root/macos"
app="$root/dist/Screen Ruler.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$root/macos/.build/release/ScreenRuler" "$app/Contents/MacOS/Screen Ruler"
cp "$root/macos/Info.plist" "$app/Contents/Info.plist"
codesign --force --deep --sign - "$app"
rm -f "$root/dist/ScreenRuler-macOS.zip"
ditto -c -k --keepParent "$app" "$root/dist/ScreenRuler-macOS.zip"
echo "Built $root/dist/ScreenRuler-macOS.zip"
