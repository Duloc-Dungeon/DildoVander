<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { account, accountOrigin, accountRequest, refreshIdentity } from '../identity/session';

type Provider = 'elevenlabs' | 'gpt-live';
type ExperimentSettings = {
  revision: number;
  provider: Provider;
  dialoguePrompt: string;
  livePrompt: string;
};
type ExperimentDefaults = Pick<ExperimentSettings, 'provider' | 'dialoguePrompt' | 'livePrompt'> & { speechDirections?: string };
type ExperimentDraft = Pick<ExperimentSettings, 'provider' | 'dialoguePrompt' | 'livePrompt'>;
type ExperimentResponse = {
  settings?: ExperimentSettings;
  defaults?: ExperimentDefaults;
  loadError?: string | null;
};

const router = useRouter();
const busy = ref(false);
const entryError = ref('');
const settings = ref<ExperimentSettings | null>(null);
const defaults = ref<ExperimentDefaults | null>(null);
const draft = ref<ExperimentDraft>({ provider: 'elevenlabs', dialoguePrompt: '', livePrompt: '' });
const settingsLoading = ref(false);
const settingsSaving = ref(false);
const settingsError = ref('');
const settingsNotice = ref('');
const conflict = ref(false);
type ResetTarget = { profileId: string; revision: number; userInput: { name: string } };
const resetTarget = ref<ResetTarget | null>(null);
const resetAccountId = ref('');
const resetBusy = ref(false);
const resetMessage = ref('');
const resetError = ref('');
const resetController = new AbortController();

async function prepareCharacterReset() {
  if (busy.value || resetBusy.value) return;
  resetBusy.value = true;
  resetTarget.value = null;
  resetMessage.value = '';
  resetError.value = '';
  try {
    const response = await postJson('/local-try-api/session', {});
    if (!response.ok || resetController.signal.aborted) throw new Error('Local session unavailable.');
    await refreshIdentity();
    if (accountOrigin.value && accountOrigin.value !== window.location.origin) throw new Error('Local account required.');
    resetAccountId.value = account.value?.id ?? '';
    const result = await accountRequest<{ profile: ResetTarget | null }>('/app/v1/customer/character', 'GET', undefined, resetController.signal);
    if (!resetAccountId.value || resetAccountId.value !== account.value?.id) throw new Error('Account changed.');
    resetTarget.value = result.profile;
    if (!result.profile) resetMessage.value = 'Žádná testovací postava není připravená. Otevři dílnu a vytvoř novou.';
  } catch {
    resetError.value = 'Postavu se nepodařilo načíst. Zkus reset znovu; místní hub musí běžet.';
  } finally {
    resetBusy.value = false;
  }
}

async function resetCharacter() {
  const target = resetTarget.value;
  if (!target || resetBusy.value || busy.value) return;
  if (resetAccountId.value !== account.value?.id || accountOrigin.value && accountOrigin.value !== window.location.origin) {
    resetTarget.value = null;
    resetError.value = 'Přihlášený účet se změnil. Načti postavu znovu.';
    return;
  }
  resetBusy.value = true;
  resetError.value = '';
  try {
    await accountRequest(`/app/v1/customer/character?expectedRevision=${target.revision}&expectedProfileId=${encodeURIComponent(target.profileId)}`,
      'DELETE', undefined, resetController.signal);
    resetMessage.value = 'Postava byla resetována. Otevři dílnu a připrav novou.';
  } catch {
    resetError.value = 'Reset se nepodařil nebo se postava mezitím změnila. Načti ji znovu a zkus to ještě jednou.';
  } finally {
    resetTarget.value = null;
    resetBusy.value = false;
  }
}

const isDirty = computed(() => !!settings.value && (
  draft.value.provider !== settings.value.provider
  || draft.value.dialoguePrompt !== settings.value.dialoguePrompt
  || draft.value.livePrompt !== settings.value.livePrompt
));
const providerLabel = (provider: Provider) => provider === 'elevenlabs' ? 'ElevenLabs v4 + GPT' : 'GPT-Live';

async function postJson(path: string, body: unknown): Promise<Response> {
  return fetch(path, {
    method: 'POST', credentials: 'same-origin', cache: 'no-store',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
}

function applyLoadedExperiments(payload: ExperimentResponse) {
  if (payload.loadError) throw new Error(payload.loadError);
  if (!payload.settings || !payload.defaults || !Number.isInteger(payload.settings.revision)) {
    throw new Error('Local prompt settings are unavailable.');
  }
  settings.value = payload.settings;
  defaults.value = payload.defaults;
  draft.value = {
    provider: payload.settings.provider,
    dialoguePrompt: payload.settings.dialoguePrompt,
    livePrompt: payload.settings.livePrompt,
  };
  conflict.value = false;
}

async function loadExperiments(discardDraft = false) {
  if (isDirty.value && !discardDraft) {
    settingsError.value = 'Ve formuláři jsou neuložené úpravy. Nejprve je ulož, nebo po konfliktu použij výslovné načtení aktuální verze.';
    return;
  }
  if (settingsLoading.value || settingsSaving.value) return;
  settingsLoading.value = true;
  settingsError.value = '';
  settingsNotice.value = '';
  try {
    const response = await postJson('/local-try-api/experiments/read', {});
    if (!response.ok) throw new Error(`Local settings request failed (${response.status}).`);
    applyLoadedExperiments(await response.json() as ExperimentResponse);
  } catch {
    settingsError.value = 'Místní nastavení promptů se nenačetlo. Spusť lokální hub jeho spouštěčem a zkus načtení znovu.';
  } finally {
    settingsLoading.value = false;
  }
}

async function saveExperiments() {
  if (!settings.value || !isDirty.value || settingsSaving.value || conflict.value) return;
  settingsSaving.value = true;
  settingsError.value = '';
  settingsNotice.value = '';
  try {
    const response = await postJson('/local-try-api/experiments', {
      expectedRevision: settings.value.revision,
      provider: draft.value.provider,
      dialoguePrompt: draft.value.dialoguePrompt,
      livePrompt: draft.value.livePrompt,
    });
    if (response.status === 409) {
      conflict.value = true;
      settingsError.value = 'Nastavení se mezitím změnilo. Neuložené úpravy zůstaly zachované; porovnej je s aktuální verzí nebo načti aktuální nastavení.';
      return;
    }
    if (!response.ok) throw new Error(`Local settings save failed (${response.status}).`);
    applyLoadedExperiments(await response.json() as ExperimentResponse);
    settingsNotice.value = 'Uloženo. Nový poskytovatel a prompty se použijí při dalším připojení nebo požadavku; probíhající rozhovor se nemění.';
  } catch {
    settingsError.value = 'Místní nastavení se neuložilo. Změny zůstaly ve formuláři; zkus uložení znovu.';
  } finally {
    settingsSaving.value = false;
  }
}

function fillDefaults() {
  if (!defaults.value || settingsLoading.value || settingsSaving.value) return;
  draft.value = {
    ...draft.value,
    dialoguePrompt: defaults.value.dialoguePrompt,
    livePrompt: defaults.value.livePrompt,
  };
  settingsNotice.value = 'Výchozí prompty jsou ve formuláři. Použij tlačítko níže, pokud je chceš uložit.';
  settingsError.value = '';
}

async function openTool(path: '/play/wand' | '/persona-lab') {
  if (busy.value || resetBusy.value) return;
  busy.value = true;
  entryError.value = '';
  try {
    const response = await postJson('/local-try-api/session', {});
    if (!response.ok) throw new Error('Local session unavailable.');
    await refreshIdentity();
    await router.push(path);
  } catch {
    entryError.value = 'Místní vstup se nepodařilo připravit. Spusť místní hub jeho spouštěčem a zkus to znovu.';
  } finally {
    busy.value = false;
  }
}

onMounted(() => { void loadExperiments(); });
onBeforeUnmount(() => resetController.abort());
</script>

<template>
  <article class="page-stack information-page local-try-hub" aria-labelledby="local-try-title">
    <p class="eyebrow">Wand Night Stand · místní prostředí</p>
    <h1 id="local-try-title">Vyzkoušet změnu</h1>
    <p>Vyber si rozhovor, který chceš po úpravě projít. Místní testovací účet se připraví automaticky, bez registrace.</p>
    <p v-if="busy" role="status">Připravujeme místní vstup…</p>
    <p v-if="entryError" role="alert">{{ entryError }}</p>

    <section class="local-try-hub__primary" aria-labelledby="workshop-title">
      <p class="eyebrow">Zákaznický rozhovor</p>
      <h2 id="workshop-title">Kufrova dílna</h2>
      <p>Stejný zákaznický rozhovor jako v mobilní aplikaci: její otázky, aktuální hlasový vstup i výstup a ukládání hůlky.</p>
      <div class="local-try-hub__actions">
        <button type="button" class="local-try-hub__link" :disabled="busy || resetBusy" @click="openTool('/play/wand')">Otevřít Kufrovu dílnu</button>
        <button type="button" class="local-try-hub__secondary" :disabled="busy || resetBusy" @click="prepareCharacterReset">Resetovat testovací postavu</button>
      </div>
      <p v-if="resetBusy" role="status">Zpracovávám místní postavu…</p>
      <div v-if="resetTarget" class="local-try-hub__reset" role="group" aria-label="Potvrzení resetu postavy">
        <p>Resetovat místní postavu <strong>{{ resetTarget.userInput.name }}</strong>? Smaže se její profil, hůlka a navázaný postup. Prompty a volba hlasu zůstanou uložené.</p>
        <div class="local-try-hub__actions">
          <button type="button" class="local-try-hub__secondary" :disabled="resetBusy" @click="resetCharacter">Potvrdit reset postavy</button>
          <button type="button" class="local-try-hub__secondary" :disabled="resetBusy" @click="resetTarget = null">Zrušit</button>
        </div>
      </div>
      <p v-if="resetMessage" role="status">{{ resetMessage }}</p>
      <p v-if="resetError" role="alert">{{ resetError }}</p>
    </section>

    <section class="local-try-hub__settings" aria-labelledby="experiment-title">
      <div class="local-try-hub__settings-heading">
        <div>
          <p class="eyebrow">Místní konfigurace</p>
          <h2 id="experiment-title">Poskytovatel a prompty rozhovoru</h2>
        </div>
        <button type="button" class="local-try-hub__secondary" :disabled="settingsLoading || settingsSaving || conflict" @click="loadExperiments()">
          {{ settingsLoading ? 'Načítám…' : 'Načíst aktuální' }}
        </button>
      </div>

      <p v-if="settings" class="local-try-hub__applied" role="status">
        Aktuálně použito: <strong>{{ providerLabel(settings.provider) }}</strong> · revize {{ settings.revision }}
      </p>
      <p class="local-try-hub__help">Nová volba poskytovatele a textové prompty se použijí až při dalším připojení nebo požadavku. Probíhající rozhovory zůstanou beze změny.</p>
      <p v-if="settingsError" role="alert">{{ settingsError }}</p>
      <p v-if="settingsNotice" class="local-try-hub__notice" role="status">{{ settingsNotice }}</p>

      <fieldset :disabled="!settings || settingsLoading || settingsSaving || conflict">
        <legend>Hlasový a dialogový poskytovatel</legend>
        <label class="local-try-hub__choice">
          <input v-model="draft.provider" type="radio" name="local-try-provider" value="elevenlabs">
          <span><strong>ElevenLabs v4 + GPT</strong><small>České rozpoznání a hlas ElevenLabs, dialogy GPT.</small></span>
        </label>
        <label class="local-try-hub__choice">
          <input v-model="draft.provider" type="radio" name="local-try-provider" value="gpt-live">
          <span><strong>GPT-Live</strong><small>Samostatná hlasová relace GPT-Live.</small></span>
        </label>

        <label class="local-try-hub__prompt-label" for="local-dialogue-prompt">GPT backend – rozhovor</label>
        <textarea id="local-dialogue-prompt" v-model="draft.dialoguePrompt" maxlength="12000" rows="10" />
        <p class="local-try-hub__count">{{ draft.dialoguePrompt.length }}/12000</p>

        <details v-if="defaults?.speechDirections" class="local-try-hub__speech-directions">
          <summary>ElevenLabs – předloha přednesu</summary>
          <p>Uložené pokyny k přednesu. TTS nemá samostatné pole pro tento prompt; kód používá hlas zvolený správcem, češtinu, rychlost 1.0 a návaznost úseků. Jemnou intonaci nelze tímto rozhraním zaručit.</p>
          <label class="local-try-hub__prompt-label" for="local-speech-directions">Schválená předloha přednesu</label>
          <textarea id="local-speech-directions" :value="defaults.speechDirections" readonly rows="8" />
        </details>

        <label class="local-try-hub__prompt-label" for="local-live-prompt">GPT-Live – hlas</label>
        <textarea id="local-live-prompt" v-model="draft.livePrompt" maxlength="12000" rows="10" />
        <p class="local-try-hub__count">{{ draft.livePrompt.length }}/12000</p>
      </fieldset>

      <div class="local-try-hub__actions">
        <button type="button" class="local-try-hub__secondary" :disabled="!defaults || settingsLoading || settingsSaving || conflict" @click="fillDefaults">Výchozí prompty</button>
        <button type="button" class="local-try-hub__link" :disabled="!settings || !isDirty || settingsSaving || settingsLoading || conflict" @click="saveExperiments">
          {{ settingsSaving ? 'Ukládám…' : 'Použít pro další připojení' }}
        </button>
      </div>
      <p v-if="conflict" class="local-try-hub__conflict-help">Načtení aktuální verze zahodí neuložené úpravy ve formuláři. Změny z jiné karty tím nepřepíšeš.</p>
      <button v-if="conflict" type="button" class="local-try-hub__secondary" :disabled="settingsLoading || settingsSaving" @click="loadExperiments(true)">Načíst aktuální nastavení a zrušit moje neuložené úpravy</button>
      <p class="local-try-hub__help">Prompty se uloží pouze do lokální konfigurace tohoto prostředí. Každý prompt může mít nejvýše 12&nbsp;000 znaků.</p>
    </section>

    <section aria-labelledby="lab-title">
      <p class="eyebrow">Oddělené testovací prostředí</p>
      <h2 id="lab-title">AI Persona Lab</h2>
      <p>Oddělené prostředí pro DV01. Jeho scénáře ani nastavení nejsou zákaznickým rozhovorem Kufrovy dílny.</p>
      <button type="button" class="local-try-hub__link" :disabled="busy || resetBusy" @click="openTool('/persona-lab')">Otevřít AI Persona Lab</button>
    </section>

    <p class="local-try-hub__note">Místní databáze a přihlášení jsou oddělené od produkce. Odeslání zprávy nebo spuštění hlasu volá skutečného poskytovatele a může stát peníze.</p>
  </article>
</template>

<style scoped>
.local-try-hub__primary,
.local-try-hub__settings {
  border: 1px solid var(--wns-mint);
  border-radius: 1rem;
  padding: clamp(1rem, 3vw, 1.5rem);
  background: var(--wns-surface);
}

.local-try-hub__primary > :first-child,
.local-try-hub > section > :first-child {
  margin-top: 0;
}

.local-try-hub__settings-heading,
.local-try-hub__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
}

.local-try-hub__choice {
  display: flex;
  align-items: flex-start;
  gap: .75rem;
  margin: .75rem 0;
  cursor: pointer;
}

.local-try-hub__choice input { margin-top: .25rem; }
.local-try-hub__choice span { display: grid; gap: .2rem; }
.local-try-hub__choice small,
.local-try-hub__help,
.local-try-hub__count,
.local-try-hub__conflict-help { color: var(--wns-muted); }
.local-try-hub__help { margin-block: .75rem; }
.local-try-hub__applied { padding: .75rem; background: var(--wns-surface-raised); border-radius: .5rem; }
.local-try-hub__prompt-label { display: block; margin-top: 1.25rem; font-weight: 700; }
.local-try-hub textarea {
  display: block;
  box-sizing: border-box;
  width: 100%;
  min-height: 14rem;
  margin-top: .5rem;
  padding: .75rem;
  border: 1px solid var(--wns-input-border);
  border-radius: .5rem;
  background: var(--wns-input-bg);
  color: var(--wns-text);
  font: inherit;
  line-height: 1.5;
  resize: vertical;
}
.local-try-hub textarea:focus-visible,
.local-try-hub__link:focus-visible,
.local-try-hub__secondary:focus-visible {
  outline: 3px solid var(--wns-focus-ring);
  outline-offset: 3px;
}
.local-try-hub__count { margin: .25rem 0; text-align: right; font-size: .875rem; }
.local-try-hub__reset { margin-top: 1rem; }
.local-try-hub__reset p { margin-bottom: 1rem; }

.local-try-hub__link,
.local-try-hub__secondary {
  display: inline-flex;
  min-height: 44px;
  align-items: center;
  padding: .5rem .875rem;
  border-radius: .5rem;
  font: inherit;
  font-weight: 700;
  cursor: pointer;
}

.local-try-hub__link {
  background: var(--wns-mint);
  color: var(--wns-on-mint);
  text-decoration: none;
  border: 0;
}

.local-try-hub__secondary {
  color: var(--wns-text);
  background: var(--wns-surface);
  border: 1px solid var(--wns-line);
}

.local-try-hub__link:disabled,
.local-try-hub__secondary:disabled { opacity: .65; cursor: not-allowed; }

.local-try-hub__note {
  border-left: 3px solid var(--wns-amber);
  padding-left: 1rem;
  color: var(--wns-muted);
}
</style>
