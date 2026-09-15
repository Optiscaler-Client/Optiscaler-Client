# Flatpak packaging (standalone bundle)

`build.sh` publishes a self-contained `linux-x64` build and wraps it into a single
`OptiscalerClient-<version>-x86_64.flatpak` bundle in the repo root, the same way
`packaging/appimage/build.sh` wraps it into an AppImage. This is wired into CI
(`.github/workflows/release.yml`) and attached to every GitHub release alongside the
other artifacts.

This is a standalone bundle, not a Flathub submission — no AppStream metainfo, no
sandboxed/offline NuGet vendoring, no signing. Install with `flatpak install
OptiscalerClient-<version>-x86_64.flatpak`.

## One-time setup (host or CI runner)

```sh
flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install -y flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08
```

Also needs `flatpak-builder` and `dotnet` (10.x SDK) on PATH.

## Usage

```sh
packaging/flatpak/build.sh
```

## Runtime dependencies

Same as the AppImage: the app bundles its own .NET runtime, so it only needs the base
libs `org.freedesktop.Platform` already ships (fontconfig, libX11, mesa/GL for Skia).
Nothing extra is bundled into the `.flatpak`.

## Notes

- Version is read straight from `OptiscalerClient.csproj`'s `<Version>`.
- `build/` (publish output, staged desktop/icon files, flatpak repo/builddir) is a local
  build cache — safe to delete, gets regenerated on the next run.
- App ID `io.github.optiscaler_client.OptiscalerClient` follows the reverse-DNS
  convention for projects without an owned domain — no functional effect for a
  standalone bundle, just avoids a rename if this ever goes to Flathub later.
