# AppImage packaging (reference)

`build.sh` publishes a self-contained `linux-x64` build and wraps it into a single
`OptiscalerClient-<version>-x86_64.AppImage` in the repo root, the same way
`packaging/aur/PKGBUILD` wraps the release tarball into an Arch package. This isn't wired
into CI (there's no GitHub Actions workflow in this repo yet) — it's a script you run
manually, same spirit as the AUR packaging.

## Requirements

- Linux or WSL. `appimagetool` is itself a Linux ELF binary, so this won't run under
  plain Git Bash on Windows.
- `dotnet` (10.x SDK) available on PATH — e.g. via `nix develop` (see `flake.nix`), or a
  distro package.
- `curl`, to fetch `appimagetool` on first run (cached under `packaging/appimage/build/`
  afterward).

## Usage

```sh
packaging/appimage/build.sh
```

## Runtime dependencies

The app bundles its own .NET runtime (self-contained publish), so the AppImage only needs
the same native libs already listed as `depends` in `packaging/aur/PKGBUILD` present on the
host: `fontconfig`, `libx11`, `libice`, `libsm`. They're not bundled into the AppImage —
virtually every desktop Linux distro already has them.

## Notes

- Version is read straight from `OptiscalerClient.csproj`'s `<Version>`, so it never needs
  bumping by hand here.
- `build/` (publish output + cached `appimagetool`) is a local build cache — safe to delete,
  gets regenerated on the next run.
