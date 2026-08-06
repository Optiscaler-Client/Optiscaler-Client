# AUR packaging (reference)

`PKGBUILD` here is a starting point for an AUR `optiscaler-client-bin` package, not something
published automatically from this repo. It downloads the official `linux-x64` release tarball
and installs it to `/opt/optiscaler-client`, with a symlink in `/usr/bin` and a `.desktop` entry.

## Testing locally

```sh
cd packaging/aur
makepkg -si
```

## Publishing / maintaining on AUR

Whoever adopts this as the actual AUR listing needs an AUR account and should, on every new
GitHub release:

1. Bump `pkgver` (and reset `pkgrel=1`).
2. Refresh checksums: `updpkgsums`.
3. Regenerate `.SRCINFO`: `makepkg --printsrcinfo > .SRCINFO`.
4. Push to the AUR git repo (`ssh://aur@aur.archlinux.org/optiscaler-client-bin.git`).

This isn't something the upstream maintainer needs to run themselves - any community member can
adopt and maintain it.
