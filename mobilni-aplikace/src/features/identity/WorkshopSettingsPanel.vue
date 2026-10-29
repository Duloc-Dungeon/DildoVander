<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountOrigin, accountRequest, hasRole } from './session';

type ReasoningEffort = 'none' | 'low' | 'medium' | 'high';
interface WorkshopSettings {
  revision: number;
  dialoguePrompt: string;
  livePrompt: string;
  reasoningEffort: ReasoningEffort;
  speechDirections: string;
}
const path = '/app/v1/admin/workshop-settings';
const reasoningOptions: { value: ReasoningEffort; label: string }[] = [
  { value: 'none', label: 'Vypnuto (výchozí)' }, { value: 'low', label: 'Nízká' },
  { value: 'medium', label: 'Střední' }, { value: 'high', label: 'Vysoká' },
];
const saved = ref<WorkshopSettings | null>(null);
const dialoguePrompt = ref(''), livePrompt = ref('');
const reasoningEffort = ref<ReasoningEffort>('none');
const busy = ref(false), conflict = ref(false), error = ref(''), message = ref('');
const verificationRequired = ref(false);
const validPrompt = (value: unknown): value is string => typeof value === 'string' && !!value.trim() && value.length <= 12000;
const valid = computed(() => validPrompt(dialoguePrompt.value) && validPrompt(livePrompt.value)
  && reasoningOptions.some(option => option.value === reasoningEffort.value));
const changed = computed(() => !!saved.value && (dialoguePrompt.value !== saved.value.dialoguePrompt
  || livePrompt.value !== saved.value.livePrompt || reasoningEffort.value !== saved.value.reasoningEffort));
let generation = 0;
let controller = new AbortController();
const identity = () => JSON.stringify([account.value?.id, account.value?.roles, accountOrigin.value]);
const current = (ticket: number, owner: string) => ticket === generation && owner === identity() && hasRole('Administrator');

function apply(result: WorkshopSettings) {
  if (!Number.isSafeInteger(result?.revision) || result.revision < 0 || !validPrompt(result.dialoguePrompt)
    || !validPrompt(result.livePrompt) || typeof result.speechDirections !== 'string'
    || !reasoningOptions.some(option => option.value === result.reasoningEffort)) throw new Error('invalid_workshop_settings');
  saved.value = result; dialoguePrompt.value = result.dialoguePrompt; livePrompt.value = result.livePrompt;
  reasoningEffort.value = result.reasoningEffort; conflict.value = false;
}
function showFailure(failure: unknown, saving: boolean) {
  const detail = failure as { status?: number; code?: string } | null;
  const conflictFailure = saving && detail?.status === 409;
  if (saving) conflict.value = conflictFailure;
  verificationRequired.value = detail?.status === 401 || detail?.status === 403;
  error.value = conflictFailure
    ? 'Nastavení se mezitím změnilo. Tvoje úpravy zůstaly ve formuláři. Pro další uložení výslovně načti uloženou verzi.'
    : detail?.code === 'confirmation_cancelled' ? 'Uložení nebylo potvrzeno. Tvoje úpravy zůstaly ve formuláři.'
    : verificationRequired.value ? 'Pro tuto změnu je potřeba znovu ověřit oprávnění správce. Po ověření odešli uložení znovu.'
    : detail?.status === 400 ? 'Nastavení nebylo přijato. Oba prompty musí obsahovat text a mít nejvýše 12 000 znaků.'
    : detail?.status === 503 ? 'Nastavení rozhovoru teď není dostupné. Zkus požadavek znovu; tvoje úpravy zůstaly ve formuláři.'
    : saving ? 'Nastavení se nepodařilo uložit. Tvoje úpravy zůstaly ve formuláři.' : 'Nastavení rozhovoru se nepodařilo načíst. Zkus načtení znovu.';
}
async function load() {
  if (busy.value || !hasRole('Administrator')) return;
  const ticket = ++generation, owner = identity();
  busy.value = true; error.value = ''; message.value = ''; verificationRequired.value = false;
  try {
    const result = await accountRequest<WorkshopSettings>(path, 'GET', undefined, controller.signal);
    if (current(ticket, owner)) apply(result);
  } catch (failure) { if (current(ticket, owner)) showFailure(failure, false); }
  finally { if (ticket === generation) busy.value = false; }
}
async function save() {
  if (!saved.value || !valid.value || !changed.value || busy.value || conflict.value || !hasRole('Administrator')) return;
  const ticket = ++generation, owner = identity();
  busy.value = true; error.value = ''; message.value = ''; verificationRequired.value = false;
  try {
    const result = await accountRequest<WorkshopSettings>(path, 'PUT', {
      expectedRevision: saved.value.revision, dialoguePrompt: dialoguePrompt.value,
      livePrompt: livePrompt.value, reasoningEffort: reasoningEffort.value,
    }, controller.signal, 'Uložit prompty a reasoning Kufrovy dílny? Nové nastavení se použije při dalším rozhovoru nebo požadavku.');
    if (!current(ticket, owner)) return;
    apply(result); message.value = 'Nastavení je uložené. Použije se při dalším rozhovoru nebo požadavku.';
  } catch (failure) { if (current(ticket, owner)) showFailure(failure, true); }
  finally { if (ticket === generation) busy.value = false; }
}
watch(identity, () => {
  generation++; controller.abort(); controller = new AbortController(); saved.value = null;
  dialoguePrompt.value = ''; livePrompt.value = ''; reasoningEffort.value = 'none';
  busy.value = conflict.value = verificationRequired.value = false; error.value = ''; message.value = '';
  if (hasRole('Administrator')) void load();
}, { flush: 'sync' });
onMounted(load);
onBeforeUnmount(() => { generation++; controller.abort(); });
</script>

<template>
  <section class="aw-card workshop-settings" aria-labelledby="workshop-settings-title" :aria-busy="busy">
    <h2 id="workshop-settings-title">Prompty Kufrovy dílny</h2>
    <p>Uprav pokyny skutečně používané při rozhovoru. Změny se použijí při dalším rozhovoru nebo požadavku.</p>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }} <RouterLink v-if="verificationRequired" :to="{ path: '/signin', query: { reauth: '1', return: '/settings/admin' } }">Znovu ověřit přihlášení</RouterLink></AwNotice>
    <AwNotice v-if="message" tone="success" role="status">{{ message }}</AwNotice>
    <form v-if="saved && hasRole('Administrator')" class="identity-form workshop-settings__form" @submit.prevent="save">
      <p class="aw-field-description">Uložená revize {{ saved.revision }}. Každý prompt může mít nejvýše 12 000 znaků.</p>
      <label for="workshop-dialogue-prompt">GPT – dialogový prompt</label>
      <textarea id="workshop-dialogue-prompt" v-model="dialoguePrompt" class="aw-input" rows="12" maxlength="12000" required :disabled="busy" aria-describedby="workshop-dialogue-help workshop-dialogue-count" />
      <p id="workshop-dialogue-help" class="aw-field-description">Pokyny pro GPT, který vede dialog s hlasem ElevenLabs nebo Speech Engine.</p>
      <p id="workshop-dialogue-count" class="aw-field-description">{{ dialoguePrompt.length }}/12000 znaků</p>
      <label for="workshop-reasoning">Reasoning GPT – úroveň uvažování</label>
      <select id="workshop-reasoning" v-model="reasoningEffort" class="aw-select" :disabled="busy" aria-describedby="workshop-reasoning-help">
        <option v-for="option in reasoningOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
      </select>
      <p id="workshop-reasoning-help" class="aw-field-description">Platí pro dialog GPT s ElevenLabs a Speech Engine. Výchozí nastavení je vypnuto.</p>
      <label for="workshop-live-prompt">GPT-Live – hlasový prompt</label>
      <textarea id="workshop-live-prompt" v-model="livePrompt" class="aw-input" rows="10" maxlength="12000" required :disabled="busy" aria-describedby="workshop-live-count" />
      <p id="workshop-live-count" class="aw-field-description">{{ livePrompt.length }}/12000 znaků</p>
      <details>
        <summary>Předloha přednesu ElevenLabs</summary>
        <p>Rozhraní TTS nepřijímá samostatný systémový prompt. Tato předloha je pouze k přečtení; dialog upravíš v promptu GPT výše.</p>
        <label for="workshop-speech-directions">Uložená předloha přednesu</label>
        <textarea id="workshop-speech-directions" class="aw-input" :value="saved.speechDirections" readonly rows="6" />
      </details>
      <AwButton type="submit" :busy="busy" :disabled="busy || !changed || !valid || conflict">Uložit nastavení rozhovoru</AwButton>
    </form>
    <AwButton v-if="hasRole('Administrator')" variant="secondary" :disabled="busy" @click="load">{{ saved ? 'Zahodit úpravy a načíst uložené nastavení' : 'Načíst nastavení rozhovoru' }}</AwButton>
  </section>
</template>

<style scoped>
.workshop-settings, .workshop-settings__form, .workshop-settings details { min-width: 0; }
.workshop-settings__form { display: grid; grid-template-columns: minmax(0, 1fr); gap: .75rem; margin-bottom: 1rem; }
.workshop-settings textarea, .workshop-settings select { width: 100%; box-sizing: border-box; }
.workshop-settings textarea { min-height: 8rem; line-height: 1.5; }
.workshop-settings summary { cursor: pointer; min-height: 44px; align-content: center; }
.workshop-settings details label { display: block; margin-bottom: .5rem; }
.workshop-settings p { overflow-wrap: anywhere; }
</style>
