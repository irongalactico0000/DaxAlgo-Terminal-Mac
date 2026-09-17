#!/usr/bin/env bash
# Fast Mac Dock launcher for local UX work (not a full Release package).
# Creates: ~/Desktop/DaxAlgo Terminal.app  (and optional ~/Applications copy)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MACOS_DIR="$REPO_ROOT/src/linux/Shell/TradingTerminal.App.Avalonia/MacOS"
PROJECT="$REPO_ROOT/src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj"
DEST_DESKTOP="${HOME}/Desktop/DaxAlgo Terminal.app"
DEST_APPS="${HOME}/Applications/DaxAlgo Terminal.app"
STAGE="$REPO_ROOT/tmp/macos-dev-launcher"
APP="$STAGE/DaxAlgo Terminal.app"
CONTENTS="$APP/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"
DOTNET_BIN="${DOTNET_BIN:-$HOME/.dotnet/dotnet}"

mkdir -p "$MACOS" "$RESOURCES" "$HOME/Applications"

# Ensure Debug build exists
export PATH="$HOME/.dotnet:$PATH"
"$DOTNET_BIN" build "$PROJECT" -c Debug -v q

# Launcher script = CFBundleExecutable
cat > "$MACOS/DaxAlgoTerminal" <<EOF
#!/bin/bash
export PATH="\$HOME/.dotnet:\$PATH"
cd "$REPO_ROOT"
exec "$DOTNET_BIN" run --project "$PROJECT" --no-build -c Debug -- --bypass-login "\$@"
EOF
chmod +x "$MACOS/DaxAlgoTerminal"

# Info.plist (LSPrincipalClass helps Dock/Activation)
cat > "$CONTENTS/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>DaxAlgo Terminal</string>
  <key>CFBundleExecutable</key>
  <string>DaxAlgoTerminal</string>
  <key>CFBundleIconFile</key>
  <string>DaxAlgoTerminal</string>
  <key>CFBundleIdentifier</key>
  <string>com.daxalgo.terminal.dev</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>DaxAlgo Terminal</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0.0-dev</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.finance</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
PLIST

# Icon (simple resample — no padColor; older macOS sips is picky)
ICONSET="$STAGE/DaxAlgoTerminal.iconset"
MASTER="$STAGE/AppIcon-1024.png"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
sips -z 1024 1024 "$MACOS_DIR/AppIcon.png" --out "$MASTER" >/dev/null
for spec in \
  "16 icon_16x16.png" "32 icon_16x16@2x.png" \
  "32 icon_32x32.png" "64 icon_32x32@2x.png" \
  "128 icon_128x128.png" "256 icon_128x128@2x.png" \
  "256 icon_256x256.png" "512 icon_256x256@2x.png" \
  "512 icon_512x512.png" "1024 icon_512x512@2x.png"; do
  size="${spec%% *}"
  name="${spec#* }"
  sips -z "$size" "$size" "$MASTER" --out "$ICONSET/$name" >/dev/null
done
iconutil --convert icns "$ICONSET" --output "$RESOURCES/DaxAlgoTerminal.icns"

# Ad-hoc sign so Gatekeeper is less annoying for local use
codesign --force --sign - "$APP" 2>/dev/null || true

rm -rf "$DEST_DESKTOP" "$DEST_APPS"
cp -R "$APP" "$DEST_DESKTOP"
cp -R "$APP" "$DEST_APPS"

# Open once so it appears in Dock; user can right-click → Keep in Dock
open "$DEST_DESKTOP"

echo "Desktop: $DEST_DESKTOP"
echo "Applications: $DEST_APPS"
echo "Pin: right-click Dock icon → Options → Keep in Dock"
