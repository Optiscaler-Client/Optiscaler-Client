# Flathub submission files

Files to copy into the `flathub/flathub` fork (branch based on `new-pr`) — they are NOT
used by `release.yml` or `../build.sh` (that one builds the standalone bundle).

- `io.github.optiscaler_client.Optiscaler_Client.yml` — builds from source, offline
- `nuget-sources.json` — vendored NuGet packages (regenerate when `PackageReference`s change):
  ```sh
  python flatpak-builder-tools/dotnet/flatpak-dotnet-generator.py \
      --runtime linux-x64 --freedesktop 25.08 --dotnet 10 \
      nuget-sources.json OptiscalerClient.csproj
  ```
- `*.metainfo.xml`, `*.desktop`, `*.png` — AppStream, launcher, 256x256 icon

Before submitting: push the release tag and replace the `commit:` placeholder in the
manifest with `git rev-parse <tag>^{commit}`.

Linter notes to mention in the PR: the `appid-url-not-reachable` error (repo name has a
hyphen) and the `finish-args-flatpak-appdata-folder` errors (the app must read/write the
Steam and Heroic Flatpak data dirs to find games and install OptiScaler into them).
