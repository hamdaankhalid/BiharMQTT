# Publishing to NuGet

BiharMQTT is published as **one package: `BiharMQTT.Server`**. It contains the whole broker (the former `BiharMQTT` core package was merged into it, under `Source/BiharMQTT.Server/Core/`). It is the only project with `IsPackable=true`. Keep it that way, and don't add new packable projects without a deliberate decision.

## Before a release

1. Update `Source/ReleaseNotes.md`. Its contents are embedded as the package release notes.
2. Make sure `README.md` is current. It is packed as the package readme (via `Directory.Build.props`).
3. Commit everything and tag the release (`git tag v<VERSION>`). The package is built from the working tree, so uncommitted changes would ship.

## Option A: script (build, pack, push)

```bash
 ./publish-nuget.sh <NUGET_API_KEY> <VERSION>    # leading space keeps the key out of zsh history (HIST_IGNORE_SPACE)
```

The API key needs the *Push* scope for `BiharMQTT.Server`. Create keys at https://www.nuget.org/account/apikeys.

## Option B: manual upload

```bash
rm -rf artifacts
dotnet build BiharMQTT.sln -c Release
dotnet pack BiharMQTT.sln -c Release --no-build --output artifacts /p:Version=<VERSION>
```

Then upload `artifacts/BiharMQTT.Server.<VERSION>.nupkg` at https://www.nuget.org/packages/manage/upload. The `.snupkg` (symbols) is optional.

## Notes

- A version number can never be reused on nuget.org, even after unlisting.
- The legacy `BiharMQTT` package should be marked **deprecated** on nuget.org with `BiharMQTT.Server` as the alternative (Manage package → Deprecation).
