# Security policy

## Supported versions

Until stable releases begin, only the latest commit on the default branch receives security fixes. Published proof-of-concept binaries are unsigned and must be verified against release checksums.

## Report vulnerabilities privately

Use GitHub's **Security → Advisories → Report a vulnerability** flow. Maintainers should enable private vulnerability reporting before making the repository public.

If private reporting is unavailable, open a minimal public issue requesting a private contact channel. Do not include exploit details, API keys, captured media, provider payloads, IPTV account information, or unredacted logs.

Include the affected commit/release, Windows edition and OS build, a reproduction using synthetic content, impact, and any proposed mitigation. Do not test against another person's device, account, playback provider, or API key.

## High-priority areas

- provider-key disclosure or accidental logging;
- frames or request bodies written to disk;
- capture continuing after stop or source loss;
- an overlay surviving a fail-open path;
- an audio session remaining altered after stop, failure, interruption, or restart;
- another process taking ownership of an active mute lease;
- public-safety content being blocked;
- budget controls being bypassed;
- requests being sent to a provider the user did not select;
- update or release artifacts being replaced or tampered with.

Ordinary classification mistakes are quality bugs unless they defeat emergency controls, suppress public-safety content, disclose private data, or leave system state altered.

## Leaked credentials

Revoke or rotate a leaked provider key immediately. Deleting it in a later commit is insufficient because it remains in Git history. Remove it from history using GitHub's secret-removal guidance, review provider usage, and enable secret scanning plus push protection.

## Release integrity

Releases should include SHA-256 checksums, the source commit/tag, the tested Windows build, dependency information, and an explicit unsigned-binary warning until code signing is introduced.
