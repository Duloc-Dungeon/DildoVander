import { ElevenLabsClient } from '@elevenlabs/elevenlabs-js';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// The SDK is confined to server tooling. Keys are never bundled into the mobile app.
const mode = process.argv[2];
if (mode === 'check') {
  if (!process.env.ELEVENLABS_API_KEY) {
    console.error('ELEVENLABS_API_KEY is not configured. Run ElevenLabs onboarding from the repository root.');
    process.exitCode = 1;
  } else {
    const timeout = setTimeout(() => {
      console.error('ElevenLabs voice access check timed out. No speech was generated.');
      process.exit(1);
    }, 20000);
    timeout.unref();
    try {
      const client = new ElevenLabsClient({ maxRetries: 0, timeoutInSeconds: 15 });
      const voiceId = process.env.Game__BossVoice__VoiceId || 'A4QaWrc6j9wnj1l191pm';
      const voice = await client.voices.get(voiceId);
      console.log(JSON.stringify({ configured: true, voiceId: voice.voiceId, name: voice.name,
        model: process.env.Game__BossVoice__Model || 'eleven_v4' }));
    } catch (error) {
      console.error('ElevenLabs voice access could not be verified. Check the key permissions and configured voice.');
      console.error(JSON.stringify({ status: Number.isInteger(error?.statusCode) ? error.statusCode : null,
        type: typeof error?.name === 'string' ? error.name : 'unknown',
        code: /^[a-z_]{1,64}$/.test(error?.body?.detail?.status ?? '') ? error.body.detail.status : null }));
      process.exitCode = 1;
    } finally {
      clearTimeout(timeout);
    }
  }
} else if (mode === 'test') {
  // Explicitly invoked, credit-consuming demo. Never run this from build/check/startup.
  const timeout = setTimeout(() => {
    console.error('Baron speech test timed out. Do not retry without checking the request.');
    process.exit(1);
  }, 45000);
  timeout.unref();
  try {
    if (!process.env.ELEVENLABS_API_KEY) throw new Error('missing_key');
    const client = new ElevenLabsClient({ maxRetries: 0, timeoutInSeconds: 40 });
    const voiceId = process.env.Game__BossVoice__VoiceId || 'A4QaWrc6j9wnj1l191pm';
    const modelId = 'eleven_v4';
    const text = '[haughty] Tak ty chceš mou dílnu? [laughing] Nejdřív si přečti záruční podmínky. '
      + '[angry] Počkej! Ten zásah nebyl v ceně! '
      + '[desperate] Ne, poslední ránu ne! Vezmi si dílnu. Ale záruční list zůstane mně.';
    const audio = await client.textToSpeech.convert(voiceId, {
      text, modelId, outputFormat: 'mp3_44100_128',
      voiceSettings: { stability: 0.5, similarityBoost: 0.75 },
    });
    const chunks = [];
    let bytes = 0;
    for await (const chunk of audio) {
      bytes += chunk.length;
      if (bytes > 512 * 1024) throw new Error('audio_too_large');
      chunks.push(chunk);
    }
    if (!bytes) throw new Error('empty_audio');
    const path = join(tmpdir(), `baron-eleven-v4-cs-${Date.now()}.mp3`);
    await writeFile(path, Buffer.concat(chunks));
    console.log(JSON.stringify({ voiceId, modelId, language: 'cs', bytes, path, text }));
  } catch (error) {
    console.error('Baron speech test failed. No automatic retry was made.');
    console.error(JSON.stringify({ status: Number.isInteger(error?.statusCode) ? error.statusCode : null,
      code: /^[a-z_]{1,64}$/.test(error?.body?.detail?.status ?? '') ? error.body.detail.status : null }));
    process.exitCode = 1;
  } finally {
    clearTimeout(timeout);
  }
} else if (mode === 'serve') {
  // Node loads the ignored root .env.local before starting ASP.NET; production
  // supplies the same variables through the existing service environment file.
  const root = fileURLToPath(new URL('../', import.meta.url));
  const child = spawn('dotnet', ['run', '--project', 'backend/backend.csproj', ...process.argv.slice(3)], {
    cwd: root, stdio: 'inherit', env: process.env,
  });
  child.on('error', () => { console.error('Could not start the .NET backend.'); process.exitCode = 1; });
  child.on('exit', code => { process.exitCode = code ?? 1; });
  for (const signal of ['SIGINT', 'SIGTERM']) {
    process.on(signal, () => child.kill(signal));
  }
} else {
  console.error('Use check (read-only), test (paid speech demo) or serve (local backend).');
  process.exitCode = 1;
}
