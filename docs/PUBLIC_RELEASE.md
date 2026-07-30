# Public release checklist

Use this checklist for every public LLM AdShield source or binary release.

## Repository hygiene

- [ ] Build from a clean clone.
- [ ] Confirm the repository contains only Windows desktop source and tests.
- [ ] Scan the full Git history for API keys, DPAPI files, signing material, captured frames, logs, provider payloads, IPTV credentials, playlists, and personal paths.
- [ ] Confirm ignored build, test-result, IDE, and local configuration files are absent.
- [ ] Verify all Markdown links and GitHub issue forms.
- [ ] Confirm CI passes without secrets and without paid API requests.

## Behavior and privacy

- [ ] Review provider endpoints, request parameters, model IDs, prices, and retention links for currency.
- [ ] Confirm the README and privacy document match actual network and storage behavior.
- [ ] Verify frames remain RAM-only and no request body appears in logs or diagnostics.
- [ ] Verify only the explicitly selected provider receives frames.
- [ ] Test stop, emergency unmute, window loss, capture failure, budget exhaustion, provider timeout, 429/5xx, and interrupted-process recovery.
- [ ] Test that public-safety verdicts release rather than acquire a block.
- [ ] Test that the exact original selected-session mute state is restored.

## Windows validation matrix

- [ ] Windows edition, version, and OS build recorded.
- [ ] x64 clean-machine launch tested.
- [ ] normal DPI plus at least one scaled-DPI display tested.
- [ ] player resize, move, minimize, restore, and exit tested.
- [ ] dedicated player and browser/helper-process audio tested.
- [ ] protected/black capture tested as unsupported.
- [ ] shadow-to-automatic workflow tested.
- [ ] tray exit and startup journal recovery tested.

## Build and package

```powershell
dotnet restore .\LLMAdShield.slnx
dotnet build .\LLMAdShield.slnx -c Release --no-restore
dotnet test .\LLMAdShield.slnx -c Release --no-build
dotnet publish .\src\LLMAdShield.Windows\LLMAdShield.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -o .\artifacts\publish\win-x64
```

- [ ] Package only the final publish directory.
- [ ] Confirm the executable is named `LLMAdShield.exe`.
- [ ] Generate SHA-256 checksums after final packaging.
- [ ] Generate an SBOM and dependency/license notice when release automation supports it.
- [ ] Scan the source archive and binary archive for secrets and personal data.
- [ ] Confirm debug logs, symbol files, test results, and recovery journals are excluded unless intentionally published and reviewed.

## Release notes

Include:

- source commit and tag;
- exact tested Windows build;
- architecture and minimum OS;
- self-contained status;
- code-signing status;
- provider endpoints and default model IDs;
- known limitations and opening-ad leakage;
- privacy and log locations;
- emergency-unmute and recovery instructions;
- SHA-256 checksums.

Until the binary is code-signed, state clearly that SmartScreen may warn and users must verify checksums before running it.
