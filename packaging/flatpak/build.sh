#!/usr/bin/env bash
#
# Builds a standalone .flatpak bundle of Optiscaler-Client from a linux-x64 publish output.
#
# Requires `flatpak` and `flatpak-builder`, plus the org.freedesktop.Platform/Sdk 24.08
# runtimes from Flathub (one-time setup, see packaging/flatpak/README.md).
#
# The app is self-contained (bundles its own .NET runtime), so this bundle only needs
# the base libs org.freedesktop.Platform already ships (fontconfig, libX11, mesa/GL).
#
# Usage: packaging/flatpak/build.sh
# Output: OptiscalerClient-<version>-x86_64.flatpak in the repo root.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
APP_ID="io.github.optiscaler_client.OptiscalerClient"
VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$REPO_ROOT/OptiscalerClient.csproj")"

BUILD_DIR="$SCRIPT_DIR/build"
PUBLISH_DIR="$BUILD_DIR/publish"
REPO_DIR="$BUILD_DIR/repo"
BUILDDIR="$BUILD_DIR/builddir"
OUT_FILE="$REPO_ROOT/OptiscalerClient-${VERSION}-x86_64.flatpak"

echo "==> Publishing OptiscalerClient v${VERSION} for linux-x64"
rm -rf "$PUBLISH_DIR"
dotnet publish "$REPO_ROOT/OptiscalerClient.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:PublishReadyToRun=true \
    -o "$PUBLISH_DIR"

echo "==> Staging desktop file and icon"
sed "s/^Exec=.*/Exec=OptiscalerClient/" "$REPO_ROOT/packaging/aur/optiscaler-client.desktop" \
    > "$BUILD_DIR/optiscaler-client.desktop"
cp "$REPO_ROOT/assets/icon.png" "$BUILD_DIR/icon.png"

echo "==> Running flatpak-builder"
rm -rf "$BUILDDIR" "$REPO_DIR"
flatpak-builder --force-clean --repo="$REPO_DIR" "$BUILDDIR" \
    "$SCRIPT_DIR/${APP_ID}.yml"

echo "==> Bundling"
rm -f "$OUT_FILE"
flatpak build-bundle "$REPO_DIR" "$OUT_FILE" "$APP_ID"

echo "==> Done: $OUT_FILE"
