<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest, hasRole } from './session';

interface ActorVoices { revision: number; kufrVoiceId: string; baronVoiceId: string }
const saved = ref<ActorVoices | null>(null);
const kufrVoiceId = ref(''), baronVoiceId = ref('');
const busy = ref(false), error = ref(''), message = ref(''), conflict = ref(false);
const changed = computed(() => !!saved.value && (saved.value.kufrVoiceId !== kufrVoiceId.value || saved.value.baronVoiceId !== baronVoiceId.value));
const valid = computed(() => [kufrVoiceId.value, baronVoiceId.value].every(value => /^[a-zA-Z0-9]{1,100}$/.test(value)));
let generation = 0;
let controller = new AbortController();
const current = (ticket: number, owner: string | undefined) => ticket === generation && owner === account.value?.id && hasRole('Administrator');

async function load() {
  if (busy.value || !hasRole('Administrator')) return;
  const ticket = ++generation, owner = account.value?.id;
  busy.value = true; error.value = ''; message.value = '';
  try {
    const result = await accountRequest<ActorVoices>('/app/v1/admin/actor-voices', 'GET', undefined, controller.signal);
    if (!current(ticket, owner)) return;
    saved.value = result; kufrVoiceId.value = result.kufrVoiceId; baronVoiceId.value = result.baronVoiceId; conflict.value = false;
  } catch (failure) {
    if (current(ticket, owner)) error.value = failure instanceof Error ? failure.message : 'Hlasy se nepodařilo načíst.';
  } finally { if (ticket === generation) busy.value = false; }
}

async function save() {
  if (!saved.value || !changed.value || !valid.value || busy.value || conflict.value || !hasRole('Administrator')) return;
  const ticket = ++generation, owner = account.value?.id;
  busy.value = true; error.value = ''; message.value = '';
  try {
    const result = await accountRequest<ActorVoices>('/app/v1/admin/actor-voices', 'PUT', {
      expectedRevision: saved.value.revision, kufrVoiceId: kufrVoiceId.value, baronVoiceId: baronVoiceId.value,
    }, controller.signal, 'Změnit hlasy Kufra a Barona? Nové hlasy se použijí od dalšího rozhovoru nebo nové promluvy.');
    if (!current(ticket, owner)) return;
    saved.value = result; kufrVoiceId.value = result.kufrVoiceId; baronVoiceId.value = result.baronVoiceId;
    message.value = 'Hlasy jsou uložené. Kufr je použije v novém spojení, Baron při nové promluvě.';
  } catch (failure) {
    if (!current(ticket, owner)) return;
    conflict.value = (failure as { status?: number }).status === 409;
    error.value = conflict.value ? 'Nastavení se mezitím změnilo. Tvoje úpravy zůstaly ve formuláři. Pro další změnu výslovně načti uloženou verzi.'
      : failure instanceof Error ? failure.message : 'Hlasy se nepodařilo uložit.';
  } finally { if (ticket === generation) busy.value = false; }
}

watch(() => `${account.value?.id ?? ''}:${account.value?.roles.join(',') ?? ''}`, () => {
  generation++; controller.abort(); controller = new AbortController(); saved.value = null;
  kufrVoiceId.value = ''; baronVoiceId.value = ''; busy.value = false; conflict.value = false; error.value = ''; message.value = '';
  if (hasRole('Administrator')) void load();
});
onMounted(load);
onBeforeUnmount(() => { generation++; controller.abort(); });
</script>

<template>
  <section class="aw-card" aria-labelledby="actor-voices-title" :aria-busy="busy">
    <h2 id="actor-voices-title">Hlasy Kufra a Barona</h2>
    <p>Každá postava může mít vlastní hlas ElevenLabs. Zadej ID hlasu dostupného v účtu služby. Výchozí hlas je Zazy.</p>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }} <RouterLink :to="{path:'/signin',query:{reauth:'1',return:'/settings/admin'}}">Znovu ověřit přihlášení</RouterLink></AwNotice>
    <AwNotice v-if="message" tone="success" role="status">{{ message }}</AwNotice>
    <form v-if="saved && hasRole('Administrator')" class="identity-form" @submit.prevent="save">
      <label for="kufr-voice-id">Hlas Kufra – ElevenLabs Voice ID</label>
      <input id="kufr-voice-id" v-model="kufrVoiceId" class="aw-input" maxlength="100" pattern="[a-zA-Z0-9]+" required :disabled="busy || conflict" autocomplete="off" spellcheck="false" />
      <label for="baron-voice-id">Hlas Barona – ElevenLabs Voice ID</label>
      <input id="baron-voice-id" v-model="baronVoiceId" class="aw-input" maxlength="100" pattern="[a-zA-Z0-9]+" required :disabled="busy || conflict" autocomplete="off" spellcheck="false" />
      <p>Kufr zachová současný hlas do konce spojení. Změna hlasu Barona vytvoří zvuk pro nový hlas.</p>
      <AwButton type="submit" :busy="busy" :disabled="busy || !changed || !valid || conflict">Uložit hlasy</AwButton>
    </form>
    <AwButton v-if="!saved || conflict" :disabled="busy" @click="load">{{ conflict ? 'Zahodit úpravy a načíst uložené hlasy' : 'Načíst hlasy' }}</AwButton>
  </section>
</template>
