<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { account, accountOrigin, accountRequest } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';
import { LabVoice, type VoicePhase } from '../persona-lab/voice';
import type { SocketTicket } from '../persona-lab/contracts';
import { projectGeneratedWand } from '../archive/generatedWand';
import GeneratedWand from '../archive/GeneratedWand.vue';

const props = defineProps<{ upgrade?: boolean }>();
const emit = defineEmits<{ created: []; 'active-change': [active: boolean] }>();
const phase = ref<VoicePhase>('idle');
const error = ref('');
const preparing = ref(false);
const operation = ref('');
const wand = ref<ReturnType<typeof projectGeneratedWand>>(null);
const transcript = ref<{role: string; text: string}[]>([]);
let epoch = 0;
let controller = new AbortController();
const voice = new LabVoice({
  phase(value) { phase.value = value; },
  transcript(role, text) {
    const previous = transcript.value.at(-1);
    if (previous?.role === role) previous.text += text;
    else transcript.value.push({role, text});
  }, turn() {}, session() {},
  wand(value) { const projected = projectGeneratedWand(value); if (projected) { wand.value = projected; } },
  error() { error.value = 'Hlasové spojení se nepodařilo dokončit. Zkontroluj připojení a povolení mikrofonu a zkus to znovu.'; },
  closed() { if (wand.value) emit('created'); },
});
const active = computed(() => preparing.value || ['connecting', 'waiting', 'listening', 'speaking'].includes(phase.value));
watch(active, value => emit('active-change', value), {flush: 'sync'});
const status = computed(() => preparing.value ? 'Připravujeme dílnu…' : ({
  idle: '', connecting: 'Připojujeme hlas…', listening: 'Mikrofon je zapnutý. Olivander tě poslouchá.',
  waiting: 'Olivander přemýšlí…', speaking: 'Olivander mluví. Můžeš ho přerušit.',
  ended: wand.value ? 'Hůlka je uložená.' : 'Rozhovor skončil. Hůlka zatím nebyla vytvořená.', error: '',
})[phase.value]);
async function start() {
  if (active.value || account.value?.openAiConsent !== true) return;
  const current = ++epoch;
  error.value = ''; preparing.value = true; transcript.value = [];
  operation.value ||= secureUuid().replaceAll('-', '');
  try {
    // Audio is unlocked by the click before awaiting account requests.
    await voice.connect(async () => {
      const session = await accountRequest<{id: string}>('/app/v1/customer/wand/sessions', 'POST', { operationId: operation.value, upgrade: props.upgrade === true }, controller.signal);
      if (current !== epoch) throw new DOMException('Zrušeno', 'AbortError');
      operation.value = session.id;
      const socketOrigin = window.location.protocol === 'capacitor:' ? 'capacitor://localhost' : window.location.origin;
      const ticket = await accountRequest<SocketTicket>(`/app/v1/customer/wand/sessions/${operation.value}/ticket`, 'POST', {socketOrigin}, controller.signal);
      preparing.value = false;
      return ticket;
    }, () => operation.value, accountOrigin.value || window.location.origin, {purpose: 'wand'});
  } catch (cause) {
    if (current === epoch && !controller.signal.aborted) {
      error.value = 'Dílnu se nepodařilo otevřít. Zkus to znovu; uložená postava i hůlka zůstávají zachované.';
      voice.dispose();
    }
  } finally { if (current === epoch) preparing.value = false; }
}
function stop() { ++epoch; controller.abort(); controller = new AbortController(); preparing.value = false; voice.end(); if (wand.value) emit('created'); }
function clear() { stop(); operation.value = ''; wand.value = null; transcript.value = []; error.value = ''; }
watch(() => account.value?.id, clear);
watch(() => props.upgrade, clear);
onBeforeUnmount(stop);
</script>

<template>
  <section class="wand-workshop" aria-labelledby="workshop-title">
    <h2 id="workshop-title">{{ upgrade ? 'Vylepšit hůlku s Olivanderem' : 'Vyrobit hůlku s Olivanderem' }}</h2>
    <p>{{ upgrade ? 'Prober s Olivanderem novou hůlku pro taktickou hru. Původní ti zůstane, dokud nebude nová hotová.' : 'Než začneš hrát, potřebuješ vlastní hůlku. Olivander se tě krátce zeptá na postavu a vytvoří ji pro tebe.' }}</p>
    <p>Jde o živý hlasový rozhovor. Povol mikrofon a poslouchej v telefonu.</p>
    <p v-if="status" role="status">{{ status }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <AwButton v-if="!active && !wand" variant="primary" @click="start">{{ error ? 'Zkusit znovu' : 'Mluvit s Olivanderem' }}</AwButton>
    <AwButton v-if="active" @click="stop">Ukončit rozhovor</AwButton>
    <GeneratedWand v-if="wand" :wand="wand" :show-artwork="false" :show-name="true" />
    <details v-if="transcript.length"><summary>Přepis rozhovoru</summary><ol><li v-for="(line, index) in transcript" :key="index"><strong>{{ line.role === 'user' ? 'Ty' : 'Olivander' }}</strong><p>{{ line.text }}</p></li></ol></details>
  </section>
</template>

<style scoped>
.wand-workshop { display: grid; gap: 1rem; max-width: 46rem; }
.wand-workshop h2,.wand-workshop p { margin: 0; }
.wand-workshop details { margin-top: 1rem; }
.wand-workshop li p { white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
