<script setup lang="ts">
import { onBeforeUnmount, ref, useId, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest } from '../identity/session';

const props = defineProps<{ profileId: string; label?: string | null }>();
const emit = defineEmits<{ saved: [label: string | null] }>();
const id = `character-label-${useId()}`;
const draft = ref('');
const busy = ref(false);
const error = ref('');
const saved = ref(false);
let request: AbortController | undefined;
let generation = 0;
watch(() => [account.value?.id, props.profileId], () => {
  generation++;
  request?.abort();
  busy.value = false;
  draft.value = props.label ?? '';
  error.value = '';
  saved.value = false;
}, { immediate: true, flush: 'sync' });
watch(() => props.label, label => { draft.value = label ?? ''; });
onBeforeUnmount(() => { generation++; request?.abort(); });

async function save(): Promise<void> {
  if (busy.value || !account.value) return;
  const attempt = ++generation;
  const controller = new AbortController();
  request = controller;
  busy.value = true;
  error.value = '';
  saved.value = false;
  try {
    const response = await accountRequest<{ profile: { label?: string | null } }>(
      '/app/v1/customer/character/label', 'PUT',
      { profileId: props.profileId, label: draft.value, expectedLabel: props.label ?? null }, controller.signal);
    if (attempt !== generation || controller.signal.aborted) return;
    emit('saved', response.profile.label ?? null);
    saved.value = true;
  } catch (failure) {
    if (attempt !== generation || controller.signal.aborted) return;
    error.value = (failure as { status?: number }).status === 409
      ? 'Postava se mezitím změnila. Obnov stránku a zkus označení uložit znovu.'
      : 'Označení se nepodařilo uložit. Zkus to znovu.';
  } finally {
    if (attempt === generation) busy.value = false;
  }
}
</script>

<template>
  <form class="page-stack" @submit.prevent="save">
    <AwField :id="id" label="Rozlišovací označení (nepovinné)" hint="Například Petr nebo U okna. Ostatní uvidí jméno postavy i označení. AI dál dostává původní jméno postavy.">
      <input :id="id" v-model="draft" class="aw-input" maxlength="80" :aria-describedby="`${id}-hint`" :disabled="busy" autocomplete="off" />
    </AwField>
    <AwButton type="submit" :busy="busy" :disabled="draft.trim() === (label ?? '')">Uložit označení</AwButton>
    <AwNotice v-if="error" tone="danger">{{ error }}</AwNotice>
    <p v-if="saved" role="status">Označení je uložené.</p>
  </form>
</template>
