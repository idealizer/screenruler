#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
swift build -c release --package-path "$root/macos"
app="$root/dist/Screen Ruler.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks"
binary="$app/Contents/MacOS/Screen Ruler"
cp "$root/macos/.build/release/ScreenRuler" "$binary"
cp "$root/macos/Info.plist" "$app/Contents/Info.plist"

toolchain="$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib/swift/macosx"
if [[ ! -d "$toolchain" ]]; then
  echo "Swift library folder was not found at $toolchain" >&2
  exit 1
fi

install_name_tool -add_rpath "@executable_path/../Frameworks" "$binary"

bundle_deps() {
  local file="$1"
  local dep name dest
  while IFS= read -r dep; do
    [[ -n "$dep" ]] || continue
    case "$dep" in
      @rpath/libswift*|*/libswift*) ;;
      *) continue ;;
    esac
    name="$(basename "$dep")"
    dest="$app/Contents/Frameworks/$name"
    if [[ ! -f "$toolchain/$name" ]]; then
      echo "Missing Swift library $name (referenced by $file)" >&2
      exit 1
    fi
    if [[ ! -f "$dest" ]]; then
      cp "$toolchain/$name" "$dest"
      chmod 755 "$dest"
      install_name_tool -id "@rpath/$name" "$dest"
      install_name_tool -add_rpath "@loader_path" "$dest"
      bundle_deps "$dest"
    fi
    if [[ "$dep" != "@rpath/$name" ]]; then
      install_name_tool -change "$dep" "@rpath/$name" "$file"
    fi
  done < <(otool -L "$file" | awk 'NR>1 { print $1 }')
}

bundle_deps "$binary"
count="$(find "$app/Contents/Frameworks" -name '*.dylib' | wc -l | tr -d ' ')"
echo "Bundled $count Swift libraries"
if [[ "$count" -lt 1 ]]; then
  echo "No Swift libraries were bundled, so the app would not launch." >&2
  otool -L "$binary" >&2
  exit 1
fi

find "$app/Contents/Frameworks" -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --sign - "$app"
rm -f "$root/dist/ScreenRuler-macOS.zip"
ditto -c -k --keepParent "$app" "$root/dist/ScreenRuler-macOS.zip"
echo "Built $root/dist/ScreenRuler-macOS.zip"
