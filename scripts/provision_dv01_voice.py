#!/usr/bin/env python3
"""Explicit operator-only prompt-to-voice provisioning; never run by app startup."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import stat
import sys
import tempfile
from urllib.error import HTTPError, URLError
from urllib.request import Request, HTTPRedirectHandler, build_opener

class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


urlopen = build_opener(NoRedirect).open

NAME = "DildoVander — The Wandmaster"
PROMPT = (
    "An original, highly expressive masculine voice belonging to an eccentric, arrogant magical wandmaker. "
    "A rich, medium-low baritone with a lightly gravelly, textured quality, precise articulation, and strong theatrical presence. "
    "He sounds like an aging master craftsman who has spent centuries dealing with incompetent customers. "
    "Naturally sarcastic, dry, impatient, mischievous, and subtly amused by his own superiority. "
    "He speaks with dynamic pacing, deliberate pauses, occasional restrained chuckles, and an effortless ability to move "
    "between quiet disdain, playful mockery, excitement, and ceremonial grandeur. The performance should be charismatic, "
    "believable, conversational, and emotionally expressive rather than synthetic, exaggerated, or constantly shouting. "
    "Prioritize intelligibility and natural delivery of Czech speech, including complex consonants and Czech names. "
    "This must be an original fictional voice, not an imitation of an existing actor, celebrity, or recognizable media character."
)
SCRIPT_HINT = (
    "Tak pojď blíž. Dílna změří dřevo; tvoje ego se mi do šuplery nevejde. "
    "Řehoři, přestaň žmoulat ten třpytivý plášť. Příliš příčetný zákazník by mě stejně znepokojoval. "
    "Dobrá… teď chvíli poslouchej. Tohle je vzácný okamžik: mistr přemýšlí. "
    "Tři stříbrné přezky, čtyři křivé křivule a pořád ani špetka trpělivosti. "
    "Výborně! Konečně zajímavá odpověď. Takovou si moje panty budou pamatovat."
)


def credential(env_file):
    key = os.environ.get("OPENAI_API_KEY", "")
    if not key and env_file:
        for line in Path(env_file).read_text(encoding="utf-8").splitlines():
            if line.startswith("OPENAI_API_KEY="):
                values = shlex.split(line.split("=", 1)[1], comments=False)
                key = values[0] if len(values) == 1 else ""
                break
    if not key:
        raise ValueError("OPENAI_API_KEY is unavailable; supply the existing private backend environment.")
    return key


def atomic_json(path, value):
    previous = path.stat() if path.exists() else None
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.chmod(temporary, stat.S_IMODE(previous.st_mode) if previous else 0o600)
        if previous and hasattr(os, "chown"):
            os.chown(temporary, previous.st_uid, previous.st_gid)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def provision(state, key, replace=False, send=urlopen):
    """The lock remains after an unknown outcome to prevent duplicate creation."""
    state = Path(state).absolute()
    if state.is_symlink() or state.parent.is_symlink():
        raise ValueError("Voice state must be an operator-owned regular path.")
    state.parent.mkdir(parents=True, exist_ok=True)
    if state.exists() and not replace:
        saved = json.loads(state.read_text(encoding="utf-8"))
        if not re.fullmatch(r"voice_[A-Za-z0-9_-]{1,122}", saved.get("customVoiceId", "")):
            raise ValueError("Existing voice state is invalid; reconcile it before replacement.")
        return saved, False
    if state.exists() and state.with_name(state.name + ".previous").exists():
        raise ValueError("Preserve the previous replacement backup before replacing again.")
    pending = state.with_suffix(state.suffix + ".pending")
    # O_EXCL serializes operator invocations; HTTP is deliberately never retried.
    descriptor = os.open(pending, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    os.close(descriptor)
    try:
        request = Request("https://api.openai.com/v1/audio/voices", method="POST",
            data=json.dumps({"type": "prompt", "name": NAME, "prompt": PROMPT,
                "model": "auto", "script_hint": SCRIPT_HINT}, ensure_ascii=False).encode("utf-8"),
            headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
        try:
            with send(request, timeout=120) as response:
                result = json.load(response)
        except HTTPError as error:
            # Rejected requests did not create a voice. Never print provider bodies.
            if error.code in (400, 401, 403, 404, 422):
                pending.unlink()
            status = error.code
            error.close()
            raise ValueError(f"OpenAI voice creation rejected: HTTP {status}. Check project credentials/access.") from None
        voice_id = result.get("id", "")
        if not re.fullmatch(r"voice_[A-Za-z0-9_-]{1,122}", voice_id):
            raise ValueError("Provider returned no valid voice ID; reconcile the pending request in the voice Playground.")
        saved = {"customVoiceId": voice_id, "name": NAME, "type": "prompt", "fallbackVoice": "vesper",
            "prompt": PROMPT, "promptSha256": hashlib.sha256(PROMPT.encode()).hexdigest(),
            "scriptHint": SCRIPT_HINT, "createdAt": result.get("created_at"), "acousticVerification": "pending"}
        if state.exists():
            backup = state.with_name(state.name + ".previous")
            if backup.exists():
                raise ValueError("Preserve the previous replacement backup before replacing again; reconcile pending voice.")
            atomic_json(backup, json.loads(state.read_text(encoding="utf-8")))
        atomic_json(state, saved)
        pending.unlink()
        return saved, True
    except (URLError, TimeoutError, OSError):
        raise ValueError("Voice creation outcome unknown. Pending marker retained; reconcile in voice Playground before retry.") from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state", required=True, help="Private durable voice JSON path (not source control)")
    parser.add_argument("--credential-env-file", help="Read existing backend credential without printing it")
    parser.add_argument("--replace", action="store_true", help="Explicitly create a replacement and preserve prior selection")
    arguments = parser.parse_args()
    try:
        saved, created = provision(arguments.state, credential(arguments.credential_env_file), arguments.replace)
        print(json.dumps({"created": created, "voiceId": saved["customVoiceId"], "state": str(Path(arguments.state).absolute()),
            "fallback": "vesper", "acousticVerification": saved.get("acousticVerification", "pending")}))
    except (ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
