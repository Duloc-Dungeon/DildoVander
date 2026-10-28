<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { account, accountRequest } from '../features/identity/session';
import AwNotice from '../components/AwNotice.vue';
import { isOwnerPlayer } from './ownerMode';

interface GroupContext {
  groups: { id: string; name: string }[];
  groupId: string | null;
  accessRevision: number;
}

const emit = defineEmits<{ contextChanged: [] }>();
const owner = computed(() => isOwnerPlayer(account.value));
const context = ref<GroupContext | null>(null);
const busy = ref(false);
const error = ref('');
let generation = 0;

watch(() => owner.value ? `${account.value?.id}:${account.value?.roles.join(',')}` : null, async () => {
  const current = ++generation;
  context.value = null;
  error.value = '';
  if (!owner.value) return;
  busy.value = true;
  try {
    const value = await accountRequest<GroupContext>('/app/v1/customer/groups');
    if (current === generation) context.value = value;
  } catch {
    if (current === generation) error.value = 'Skupiny se nepodařilo načíst.';
  } finally {
    if (current === generation) busy.value = false;
  }
}, { immediate: true });

async function changeGroup(event: Event): Promise<void> {
  const groupId = (event.target as HTMLSelectElement).value;
  if (!context.value || groupId === context.value.groupId || busy.value) return;
  const current = generation;
  busy.value = true;
  error.value = '';
  try {
    const value = await accountRequest<GroupContext>('/app/v1/customer/groups/active', 'PUT', {
      groupId, expectedRevision: context.value.accessRevision,
    });
    if (current !== generation) return;
    context.value = value;
    emit('contextChanged');
  } catch {
    if (current === generation) {
      error.value = 'Skupinu se nepodařilo přepnout. Načti stránku znovu a zkus to znovu.';
      (event.target as HTMLSelectElement).value = context.value?.groupId ?? '';
    }
  } finally {
    if (current === generation) busy.value = false;
  }
}
</script>

<template>
  <section v-if="owner" class="owner-player-controls" aria-label="Skupina hráčů">
    <label v-if="context?.groups.length">Skupina hráčů
      <select :value="context.groupId ?? ''" :disabled="busy" @change="changeGroup">
        <option v-if="!context.groupId" value="" disabled>Vyber skupinu</option>
        <option v-for="group in context.groups" :key="group.id" :value="group.id">{{ group.name }}</option>
      </select>
    </label>
    <p v-else-if="context">Zatím tu není žádná skupina. Otevři administraci účtu a vytvoř ji.</p>
    <p v-else-if="busy" role="status">Načítám skupiny…</p>
    <AwNotice v-if="error" tone="danger">{{ error }}</AwNotice>
  </section>
</template>

<style scoped>
.owner-player-controls { position: relative; z-index: 2; display: grid; gap: .5rem; margin: calc(var(--safe-area-inset-top, env(safe-area-inset-top, 0px)) + .5rem) 1rem .5rem; padding: .75rem 1rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); background: var(--aw-surface); }
label { display: grid; gap: .35rem; }
select { width: 100%; min-height: 2.75rem; }
</style>
