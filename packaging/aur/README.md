# AUR packaging (reference)

The actual AUR listing already exists and is maintained by the community:
<https://aur.archlinux.org/packages/optiscaler-client-bin> (maintainer `nasir91`). This is not
where that package's source lives — it has its own git repo on AUR. `PKGBUILD` here is kept only
as documentation of how a "-bin" package like this is put together; it's not published anywhere,
and it should **not** be pushed as a new `optiscaler-client-bin` package since that name is already
taken. It downloads the official `linux-x64` release tarball and installs it to
`/opt/optiscaler-client`, with a symlink in `/usr/bin` and a `.desktop` entry.

## Testing locally

```sh
cd packaging/aur
makepkg -si
```

## If the AUR package ever needs a new maintainer

If the existing `optiscaler-client-bin` package is ever abandoned (flagged out-of-date with no
response), the correct move is to request adoption of that same package from its AUR page, not to
publish a new one under a different name. Whoever takes it over should, on every new GitHub
release:

1. Bump `pkgver` (and reset `pkgrel=1`).
2. Refresh checksums: `updpkgsums`.
3. Regenerate `.SRCINFO`: `makepkg --printsrcinfo > .SRCINFO`.
4. Push to the AUR git repo (`ssh://aur@aur.archlinux.org/optiscaler-client-bin.git`).
