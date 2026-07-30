# Changelog

All notable changes to LLM AdShield will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). This project does not yet promise semantic-versioning compatibility.

## [Unreleased]

### Changed

- Prepared the repository for its first public Windows-only release.

## [0.2.0] - 2026-07-30

### Added

- OpenAI and Anthropic provider selection with DPAPI-protected per-user API-key storage.
- Windows Graphics Capture source selection and RAM-only three-frame classification.
- Strict structured verdicts, local hysteresis, minimum ad hold, lease renewal, and conservative program release.
- Per-session Core Audio muting with persist-before-mutate recovery journaling.
- Opaque click-through overlay excluded from capture.
- Shadow mode, automatic mode, metadata-only annotations, tray controls, and emergency unmute.
- Per-run cost warning and hard budget cap.
- Automated core, provider, audio, overlay, and coordinator tests.

### Changed

- Renamed the project, solution, assemblies, executable, namespaces, storage paths, and visible branding to LLM AdShield.
- Reduced the repository to the supported Windows desktop application only.

### Security

- Provider errors, stale results, black/protected capture, budget exhaustion, source loss, and cleanup failures fail open.
- API keys are excluded from normal settings and telemetry.
