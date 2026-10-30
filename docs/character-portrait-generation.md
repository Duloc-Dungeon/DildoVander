# Character portraits and character keepsakes

Character portraits can use Gemini Nano Banana 2.1 through the direct Google
Interactions API. The existing OpenAI text provider proposes three distinct
character-specific visual jokes and selects one before the image request. The
image brief preserves the named character, fictional world and public appearance.
It asks for a detailed oil painting, rich pigments, brushwork and chiaroscuro,
with one gentle absurd situation played seriously. It does not request a photo,
flat cartoon or a generic funny hat for every character.

Configuration is explicit and defaults remain compatible with existing installs:

| Setting | Value for Gemini |
| --- | --- |
| `GEMINI_API_KEY` | Server-only credential |
| `CustomerCharacters__PortraitProvider` | `gemini` |
| `Chronicle__ImageProvider` | `gemini` |
| `Keepsake__CharacterImageProvider` | `gemini` |

Chronicle and keepsake provider modes retain their existing enable/fake gates.
The separate image selectors affect character imagery only. Wand generation,
immutable wand artwork, story text, speech and game adjudication keep their
existing providers. Chronicle illustrations use the event's frozen references
and confirmed outcomes; they do not consult a later character profile or invent
a new result to make a joke. Consented photo portraits retain the uploaded face
and age and pass only the normalized reference and approved visual theme.

The image call uses `store: false`, 1K JPEG output and a fixed Google endpoint,
with the credential only in a request header. Output is bounded and decoded
before re-encoding as WebP for character portraits or PNG for keepsakes. Thought
blocks, signatures and provider error messages are not persisted by production
code. One attempt submits one image request; refusals and timeouts never trigger
another paid generation automatically. Existing publication, consent, revision,
deletion fences and source-attributed fallback remain in place.

Account AI consent version 3 discloses Google alongside the existing providers.
Queued character and chronicle work checks current consent before Google dispatch.
Photo keepsakes require the separate `dv10-portrait-v2` consent; historical v1
consent continues to authorize only the original OpenAI route.

## Reviewed backups and refusal behavior

`backend/Assets/CharacterPortraits/catalog.json` records the exact public prompt,
provider, SHA-256 and editorial review for each packaged WebP. There are 29
reviewed portraits for the requested 30 slots: **three distinct Dementors**
(Christmas gift, gardening and frozen tea), plus 26 other characters. Hermione
was refused by both tested generators and has no packaged substitute. Exact
names and Czech aliases resolve only within the Harry Potter world; an explicit
alternate character version never silently receives a default backup. Dementor
selection is stable for a profile revision and varies across profiles.

Twenty-seven assets came from direct Nano Banana 2.1. Bellatrix and Lockhart came
from the built-in image generator after direct generation failed. The catalog
contains their actual prompts; no actor photograph or unrelated character is
used as a generated substitute. The seed remains the complete requested roster
in `scripts/portraits/backup-scenes.json`. Explicit regeneration uses:

```powershell
dotnet run --project backend-tests/BackendTests.csproj -- --generate-portrait-backups scripts/portraits/backup-scenes.json OUTPUT_DIRECTORY
```

Inspect each image before packaging it and setting `reviewed: true`. Existing
outputs are reused; failed generations are not automatically retried. The paid
Harry application-provider check succeeded with a recognizable painted portrait.
Image-reference chronicle generation can still be refused or time out. In that case, when
frozen references exist, the app locally composes those exact portraits and the
confirmed event text into the card, without another paid request. It does not
pretend that a new scene was generated. Without references, the failure remains
visible through the existing retry flow.

No safety thresholds are lowered. Google's documented optional-filter defaults
do not disable its built-in image protections; configuration is not a guarantee
that a named character will generate successfully.

Local checks:

```powershell
dotnet run --project backend-tests/BackendTests.csproj -- --character-portraits
dotnet run --project backend-tests/BackendTests.csproj -- --keepsake
dotnet run --project backend-tests/BackendTests.csproj -- --chronicle
```

Explicit paid diagnostics (never run by normal CI):

```powershell
dotnet run --project backend-tests/BackendTests.csproj -- --gemini-portrait-live OUTPUT.webp 'Harry Potter' 'Harry Potter' 'Public fictional appearance only.'
dotnet run --project backend-tests/BackendTests.csproj -- --gemini-chronicle-live REFERENCE.png OUTPUT.png
```

Portrait diagnostics also save the synthetic request beside the output for
editorial comparison and a bounded, credential-redacted error file on failure.
Use synthetic fixtures only. Never commit credentials or generated diagnostics.
An available model or successful metadata request does not establish successful
image generation: inspect actual paid outputs for likeness and a readable joke.
Provider moderation can still reject a character; report that honestly rather
than labelling a sourced fallback as a generated portrait.

API references: [image generation](https://ai.google.dev/gemini-api/docs/image-generation)
and [Interactions](https://ai.google.dev/api/interactions-api).
