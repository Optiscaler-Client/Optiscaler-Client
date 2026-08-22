#!/usr/bin/env bash
#
# Builds a self-contained AppImage of Optiscaler-Client from a linux-x64 publish output.
#
# Must run on Linux (or WSL) — appimagetool is itself a Linux ELF binary, it won't run
# under plain Git Bash on Windows. `dotnet` must be installed in whatever environment
# runs this script (e.g. `nix develop` from the repo's flake.nix, or a distro package).
#
# The app is self-contained (bundles its own .NET runtime), so this AppImage only needs
# the native libs already listed as PKGBUILD deps (fontconfig, libx11, libice, libsm) to
# be present on the host — see packaging/aur/PKGBUILD. It does not bundle them itself.
#
# Usage: packaging/appimage/build.sh
# Output: OptiscalerClient-<version>-x86_64.AppImage in the repo root.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
APP_NAME="OptiscalerClient"
DESKTOP_ID="optiscaler-client"
VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$REPO_ROOT/OptiscalerClient.csproj")"

BUILD_DIR="$SCRIPT_DIR/build"
PUBLISH_DIR="$BUILD_DIR/publish"
APPDIR="$BUILD_DIR/${APP_NAME}.AppDir"
OUT_FILE="$REPO_ROOT/${APP_NAME}-${VERSION}-x86_64.AppImage"
APPIMAGETOOL="$BUILD_DIR/appimagetool-x86_64.AppImage"

echo "==> Publishing ${APP_NAME} v${VERSION} for linux-x64"
rm -rf "$PUBLISH_DIR"
dotnet publish "$REPO_ROOT/OptiscalerClient.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:PublishReadyToRun=true \
    -o "$PUBLISH_DIR"

echo "==> Assembling AppDir"
rm -rf "$APPDIR"
install -dm755 "$APPDIR/usr/bin"
cp -r "$PUBLISH_DIR"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/${APP_NAME}"

# Reuse the AUR .desktop file as the single source of truth for Name/Comment/Categories —
# only Exec needs overriding, since inside the AppImage the binary keeps its C# assembly
# name (OptiscalerClient) rather than the AUR package's lowercase /usr/bin symlink name.
sed "s/^Exec=.*/Exec=${APP_NAME}/" "$REPO_ROOT/packaging/aur/optiscaler-client.desktop" \
    > "$APPDIR/${DESKTOP_ID}.desktop"

install -Dm644 "$REPO_ROOT/assets/icon.png" "$APPDIR/${DESKTOP_ID}.png"
ln -sf "${DESKTOP_ID}.png" "$APPDIR/.DirIcon"

cat > "$APPDIR/AppRun" <<EOF
#!/usr/bin/env bash
HERE="\$(dirname "\$(readlink -f "\${0}")")"
exec "\$HERE/usr/bin/${APP_NAME}" "\$@"
EOF
chmod +x "$APPDIR/AppRun"

if [ ! -x "$APPIMAGETOOL" ]; then
    echo "==> Downloading appimagetool"
    mkdir -p "$BUILD_DIR"
    curl -fL -o "$APPIMAGETOOL" \
        https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$APPIMAGETOOL"
fi

echo "==> Building AppImage"
rm -f "$OUT_FILE"
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUT_FILE"

echo "==> Done: $OUT_FILE"
