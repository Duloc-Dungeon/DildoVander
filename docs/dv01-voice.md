# DV01 and GPT-Live voice operations

DV01 (`dv01`) is the only executable suitcase personality. Its authoritative
instructions are `CharacterConfiguration.Persona`; Adult and Chaos are tone
variations of that character. New conversations freeze the DV01 director,
consent ceiling, finale profile, prompt revision, and voice configuration.
Historical `party-original` and `ollivander-trunk` records and original prompt
resources remain evidence. Persisted historical personality settings do not
select runtime behavior. Guest-created fictional character profiles are separate.

GPT-Live `gpt-live-1` uses the official WebSocket `session.start` protocol,
24 kHz mono PCM, and client delegation. The OpenAI .NET SDK remains pinned to
2.14.0 for supported backend APIs and dormant Realtime recovery code. The official
model documentation checked on 25 October 2026 lists `gpt-live-1` as the current
snapshot; no unverified model or protocol upgrade was made.

## Provision once, reuse the ID

Run `python scripts/provision_dv01_voice.py --state ABSOLUTE_PRIVATE_VOICE_JSON`
in an operator environment with the existing `OPENAI_API_KEY`. Alternatively,
`--credential-env-file ABSOLUTE_PRIVATE_BACKEND_ENV` reads that existing key in
memory. Keys are never printed or written to the voice record. Do not put the
record, backend environment, or credential into an app bundle or public repository.

The command creates **DildoVander — The Wandmaster** with `POST /v1/audio/voices`,
`type: prompt`, `model: auto`, the owner-authored description, and representative
Czech `script_hint` speech. It reuses an existing valid state file without a
provider call. It never runs at startup. `--replace` explicitly creates a new
candidate and preserves the previous record; retain that backup before another
replacement. Requests are not retried. An uncertain outcome retains a `.pending`
marker: reconcile the saved voices in OpenAI's voice Playground before removing
that marker or making another request. A successful API response contains metadata,
not preview audio, and does not establish Czech acoustic quality or uniqueness.

Configure the backend with either:

```text
Backend__GptLive__Voice__ConfigurationPath=ABSOLUTE_PRIVATE_VOICE_JSON
Backend__GptLive__Voice__FallbackVoice=vesper
```

or `Backend__GptLive__Voice__CustomVoiceId=voice_RETURNED_ID`. The explicit ID
takes precedence over the file. A private provisioning file persists across source
deployments only when its path is configured in the operator-owned durable service
environment. Use the private deployment runbook for that host boundary.

Configuration is read at backend startup. Controlled replacement requires the
normal authorized backend restart, and affects only newly created sessions.
Existing sessions retain their frozen voice. Invalid/missing private files or IDs
use the configured built-in fallback; `/v1/diagnostics.liveVoice` reports the
configuration status without exposing credentials. An invalid fallback name fails
configuration validation rather than silently choosing another voice.

Custom voices serialize as `audio.output.voice = { "id": "voice_..." }` in the
actual Live session; built-ins serialize as strings. Client `voice` fields remain
accepted transport compatibility metadata and cannot override server selection.
Prompt-generated voices are never sent to Realtime or standalone TTS.

An explicit custom-voice rejection before `session.started` permits one fresh
GPT-Live connection with `vesper`. Authentication, capacity, malformed protocol,
and ordinary provider failures are surfaced. No error switches engines. A voice
failure after startup ends that session through the existing failure/cancellation
path; recovery starts a new session. Voice selection never changes mid-session.

## Voice and experience acceptance

Creation of the initial candidate succeeded on 25 October 2026. The selected ID
is held in private configuration; it is intentionally not embedded in source.
The record remains `acousticVerification: pending`. No paid Live evaluation was
run as part of creation. Before accepting the sound, listen in an explicitly
authorized Live test to the Czech script retained in the record, including
Řehoř, třpytivý, šuplera, consonant clusters, a restrained sarcastic callback,
quiet pauses, excitement, and ceremonial delivery. Check intelligibility,
rhythm, interruption behavior, and emotional range with Czech listeners.
Keep the app's disclosure that speech is AI-generated.

Voice creation does not solve unsegmented Live audio attribution. `Blocked`
remains the default finale profile. `Limited` retains its no-spoken-identity
checks; `Named` remains unavailable. Audio/transcripts/playback acknowledgments
never prove a physical action. ESP32 retains movement and Stop authority;
offline manual controls do not depend on a voice or OpenAI access.

## Persona Lab and retained backup

New Lab/testing sessions and revisions use only GPT-Live and the DV01 baseline.
Revision marker `dv01-live-v1` prevents previously published historical prompts
from overriding the new baseline. Old revision/session diagnostics remain
readable, but old revisions and Realtime sessions cannot execute or publish.
Authors can revise, test, compare and publish new DV01 prompt revisions.

Realtime 2.1 sources, SDK dependencies, protocol tests, and shared media types
remain available for deliberate engineering recovery. Its production start route
returns `410 conversation_engine_upgrade_required`; device media and Persona Lab
reject it before provider admission. Retained audio/playback route aliases support
shared transport compatibility and cannot create a Realtime session.

Offline validation uses fake provider sockets and deterministic replay. Acoustic,
installed iPhone, and physical-suitcase acceptance require their actual surfaces.

References: [custom voices](https://developers.openai.com/api/docs/guides/custom-voices),
[voice creation](https://developers.openai.com/api/reference/resources/audio/subresources/voices/methods/create),
[GPT-Live model](https://developers.openai.com/api/docs/models/gpt-live-1),
[Live sessions](https://developers.openai.com/api/docs/guides/live-conversations),
[Live prompting](https://developers.openai.com/api/docs/guides/live-prompting).

Standalone Show TTS and Responses-generated duel flavor are dormant. Outside an
active Live actor, ambient speech preview and standalone scene/duel narration
report `gpt_live_required`; they do not synthesize a second voice. Active Live
consumes existing Show direction hooks. Typed scene effects and approved offline
clips remain available. This is explicit retirement of standalone narration, not
proof of a newly implemented Live narration session for every Show workflow.
