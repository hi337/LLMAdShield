# Privacy and data handling

LLM AdShield minimizes retained data, but cloud classification is not offline. It sends sampled images from the user-selected player window to the LLM provider the user explicitly selects.

Use the application only with content and playback services you are authorized to access. Do not use it on a window likely to display confidential work, authentication data, private messages, medical information, financial information, or child-related content.

## Visual data flow

1. Windows Graphics Capture reads the selected top-level window.
2. Frames are sampled locally, resized to at most 640×360, and held in a bounded ten-second RAM-only ring.
3. The latest three ordered frames are Base64-encoded inside an in-memory HTTPS request.
4. The selected provider returns a structured verdict.
5. Local code applies confirmation, hold, release, public-safety, and budget policy.
6. Frame buffers are disposed after use and when the run stops.

LLM AdShield does not intentionally write frames, thumbnails, recordings, Base64 request bodies, prompts, or raw provider responses to disk.

## Network destinations

| Selected provider | Destination |
| --- | --- |
| OpenAI | `https://api.openai.com/v1/responses` |
| Anthropic | `https://api.anthropic.com/v1/messages` |

There is no silent provider fallback. The application does not send IPTV credentials, playlists, provider account identifiers, local logs, microphone audio, or viewing history as separate fields. The selected screen images can nevertheless contain faces, captions, names, channel graphics, account UI, or notifications already visible inside the player window.

OpenAI requests use `store: false`. This does not guarantee zero retention. Provider abuse-monitoring, legal, regional, contractual, and organization-level settings still apply. Review current provider documentation before using sensitive content.

## Local credentials

Saved API keys are encrypted with Windows DPAPI for the current Windows account and stored under:

```text
%LOCALAPPDATA%\LLMAdShield\secrets
```

The application does not write plaintext keys to logs or ordinary settings. DPAPI reduces accidental disclosure but cannot protect a key from malware or a debugger running as the same user. Use a dedicated provider project/workspace with low limits and easy revocation.

Environment variables are supported as an explicit development fallback. They remain visible to the launching process and its children and should not be stored in shell history or committed files.

## Metadata logs

Each run writes a JSONL file under:

```text
%LOCALAPPDATA%\LLMAdShield\logs
```

Records may contain:

- timestamps and run phases;
- verdict, transition, confidence, and evidence enums;
- latency and error codes;
- token counts and estimated cost;
- state-machine actions and reasons;
- manual ad/program annotations.

Records do not contain frames, Base64 payloads, API keys, raw request/response bodies, prompt text, audio, transcripts, IPTV credentials, or playlists.

To remove completed telemetry, fully exit LLM AdShield and delete the `logs` directory.

## Audio recovery journal

Before changing the selected session, LLM AdShield stores the exact original mute state in:

```text
%LOCALAPPDATA%\LLMAdShield\audio-mute-recovery.json
```

This file contains audio-session identity and mute-state metadata, not audio. Keep it until restoration succeeds. The application removes it after a complete restore. Deleting it while a session is still muted can prevent automatic recovery.

## Audio and transcription

The released application does not upload or record audio and never captures the microphone. Optional playback-audio transcription is design-only. It must remain off until the separate consent, process-scoped capture, RAM-only buffering, local-transcription, and failure tests in [`AUDIO_ASSIST.md`](AUDIO_ASSIST.md) are implemented.

## Failure behavior

Unknown content, stale results, provider refusal, malformed output, network errors, capture loss, and budget exhaustion cannot begin a mute. Active leases expire and cleanup attempts to remove the overlay and restore the original session state.

LLM AdShield is an experimental convenience tool, not a privacy, accessibility, parental-control, emergency-alert, or security boundary. Classification mistakes remain possible.
