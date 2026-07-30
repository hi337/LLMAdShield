# Contributing to LLM AdShield

LLM AdShield is an experimental, user-controlled Windows viewing tool. Contributions are welcome when they preserve its fail-open behavior, privacy boundaries, lawful-use scope, and exact audio restoration.

## Project boundaries

Do not submit changes that bypass DRM or protected surfaces, intercept credentials or streams, weaken access controls, capture the microphone, silently broaden capture beyond the selected window, change master volume, or send content to a provider the user did not select.

Never commit API keys, DPAPI blobs from a real account, captured frames, recordings, transcripts, raw provider responses, IPTV credentials, playlists, viewing logs, crash dumps containing content, or copyrighted broadcast fixtures. Tests must use synthetic data.

Open an issue before a large architectural change. Report vulnerabilities privately according to [`SECURITY.md`](SECURITY.md).

## Development setup

Requirements:

- Windows 11 x64
- the .NET 10 SDK selected by [`global.json`](global.json)
- PowerShell
- optional Visual Studio with the .NET desktop workload

From the repository root:

```powershell
dotnet restore .\LLMAdShield.slnx
dotnet build .\LLMAdShield.slnx --configuration Release --no-restore
dotnet test .\LLMAdShield.slnx --configuration Release --no-build
```

CI must pass without API keys and must never call a paid provider endpoint.

## Where changes belong

- Put platform-independent verdict, budget, frame-ownership, and state-machine work in `src/LLMAdShield.Core`.
- Put WPF, Windows Graphics Capture, Core Audio, overlay, DPAPI, HTTP-provider, and tray behavior in `src/LLMAdShield.Windows`.
- Put pure policy tests in `tests/LLMAdShield.Core.Tests`.
- Put Windows, provider-contract, and coordinator tests in `tests/LLMAdShield.Windows.Tests`.

Preserve the boundaries `ICaptureSource`, `IAdClassifier`, `IDetectionStateMachine`, `IAudioMuteLease`, and `IOverlayPresenter`.

## Safety requirements

Any change that can cover a window or alter audio must include tests showing:

- original state is persisted before mutation;
- stop and disposal restore the exact state;
- partial setup rolls back safely;
- source loss, stale results, provider errors, and budget exhaustion fail open;
- emergency unmute works even while a request is active;
- repeated cleanup is idempotent;
- no captured content is written to logs or temp files.

Do not weaken a capability check to make unsupported protected capture appear functional.

## Pull requests

A focused pull request should include:

- a clear user-visible outcome;
- tests for new policy and failure paths;
- documentation for changed data flow, permissions, providers, models, prices, storage, or network destinations;
- the Windows version/build used for manual integration testing;
- confirmation that no secrets or captured content are included;
- `dotnet build` and `dotnet test` results.

Keep formatting-only changes separate from behavior changes when practical. By contributing, you agree that your contribution is licensed under the repository's MIT License.
