#!/usr/bin/env bash
# Builds PartLock for macOS on a Mac (GitHub's macos runners): one PartLock.app for Apple Silicon
# and Intel, ad-hoc signed, plus PartLock-mac.zip (for in-app updates) and PartLock.dmg (drag to
# Applications, with a README link to Apple's "open an app from an unknown developer" article).
#
# Output: mac-dist/PartLock.dmg, mac-dist/PartLock-mac.zip
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
OUT="$ROOT/mac-dist"
WORK="$ROOT/mac-build"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/PartLock/PartLock.csproj | head -1)
# Mac build number: lets Mac-only fixes go out inside the same release (the updater compares it).
MAC_BUILD="${MAC_BUILD:-${GITHUB_RUN_NUMBER:-0}}"
FULL_VERSION="$VERSION.$MAC_BUILD"
GIT_LFS_VERSION=3.5.1
GCM_VERSION=2.6.1
README_NAME="README - How to open PartLock.webloc"
APPLE_HELP="https://support.apple.com/en-us/102445"
echo "Building PartLock $VERSION (Mac build $MAC_BUILD) for macOS"

rm -rf "$OUT" "$WORK"
mkdir -p "$OUT" "$WORK/dl"

# ---- 1. The app for each CPU (single file + shared universal libraries)
for rid in osx-arm64 osx-x64; do
  dotnet publish src/PartLock.Mac/PartLock.Mac.csproj -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -p:Version="$VERSION" -p:AssemblyVersion="$FULL_VERSION" -p:FileVersion="$FULL_VERSION" -o "$WORK/publish-$rid" -nologo
done

APP="$WORK/PartLock.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$WORK/publish-osx-arm64/PartLock" "$APP/Contents/MacOS/PartLock-arm64"
cp "$WORK/publish-osx-x64/PartLock" "$APP/Contents/MacOS/PartLock-x64"
for lib in libAvaloniaNative.dylib libHarfBuzzSharp.dylib libSkiaSharp.dylib; do
  cmp "$WORK/publish-osx-arm64/$lib" "$WORK/publish-osx-x64/$lib"   # must be the same universal file
  cp "$WORK/publish-osx-arm64/$lib" "$APP/Contents/MacOS/$lib"
done
cp mac/launcher.sh "$APP/Contents/MacOS/PartLock"
chmod +x "$APP/Contents/MacOS/"*
sed -e "s/@VERSION@/$VERSION/g" -e "s/@BUILD@/$FULL_VERSION/g" mac/Info.plist.in > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"

# ---- 2. Icon from the team logo
ICONSET="$WORK/PartLock.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s src/PartLock/Assets/logo.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) src/PartLock/Assets/logo.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/PartLock.icns"

# ---- 3. Bundled Git LFS and Git Credential Manager (both CPUs)
for arch in arm64 x64; do
  lfsarch=$([ "$arch" = x64 ] && echo amd64 || echo arm64)
  dest="$APP/Contents/Resources/tools/$arch"
  mkdir -p "$dest/gcm" "$WORK/dl/$arch"
  curl -fsSL -o "$WORK/dl/$arch/lfs.zip" "https://github.com/git-lfs/git-lfs/releases/download/v$GIT_LFS_VERSION/git-lfs-darwin-$lfsarch-v$GIT_LFS_VERSION.zip"
  (cd "$WORK/dl/$arch" && unzip -q lfs.zip)
  cp "$WORK/dl/$arch/git-lfs-$GIT_LFS_VERSION/git-lfs" "$dest/git-lfs"
  curl -fsSL -o "$WORK/dl/$arch/gcm.tgz" "https://github.com/git-ecosystem/git-credential-manager/releases/download/v$GCM_VERSION/gcm-osx-$arch-$GCM_VERSION.tar.gz"
  tar xzf "$WORK/dl/$arch/gcm.tgz" -C "$dest/gcm"
  chmod +x "$dest/git-lfs" "$dest/gcm/git-credential-manager"
done

# ---- 4. Ad-hoc signature (Apple Silicon refuses to run unsigned code; this isn't an Apple Developer ID)
xattr -cr "$APP"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# ---- 5. Smoke tests on this runner
"$APP/Contents/Resources/tools/$( [ "$(uname -m)" = arm64 ] && echo arm64 || echo x64 )/git-lfs" version
"$APP/Contents/Resources/tools/$( [ "$(uname -m)" = arm64 ] && echo arm64 || echo x64 )/gcm/git-credential-manager" --version
SHOT="$WORK/smoke.png"
( "$APP/Contents/MacOS/PartLock" --demo --screenshot "$SHOT" & PID=$!; for _ in $(seq 1 60); do [ -s "$SHOT" ] && break; sleep 1; done; kill $PID 2>/dev/null || true ) || true
if [ -s "$SHOT" ]; then echo "Smoke test: app started and rendered its window ($(stat -f%z "$SHOT") bytes)"; cp "$SHOT" "$OUT/smoke-test.png"
else echo "::warning::Smoke test: no screenshot from the app"; fi

# ---- 6. Zip for in-app updates
(cd "$WORK" && ditto -c -k --keepParent PartLock.app "$OUT/PartLock-mac.zip")

# ---- 7. DMG: app + Applications shortcut + README link
STAGE="$WORK/dmg"
mkdir -p "$STAGE"
ditto "$APP" "$STAGE/PartLock.app"
ln -s /Applications "$STAGE/Applications"
cat > "$STAGE/$README_NAME" <<WEBLOC
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>URL</key><string>$APPLE_HELP</string></dict></plist>
WEBLOC
python3 -m venv "$WORK/venv"
"$WORK/venv/bin/pip" install --quiet ds_store mac_alias
"$WORK/venv/bin/python" mac/dmg_layout.py "$STAGE" "$README_NAME"
hdiutil create -volname "PartLock" -srcfolder "$STAGE" -ov -format UDZO -fs HFS+ "$OUT/PartLock.dmg"

# Check the DMG mounts and has what it should.
MNT=$(mktemp -d)
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$OUT/PartLock.dmg" >/dev/null
ls -la "$MNT"
test -x "$MNT/PartLock.app/Contents/MacOS/PartLock" && test -L "$MNT/Applications" && test -f "$MNT/$README_NAME"
hdiutil detach "$MNT" >/dev/null

echo "$FULL_VERSION" > "$OUT/PartLock-mac.version"
ls -la "$OUT"
echo "Done: PartLock $VERSION for macOS"
