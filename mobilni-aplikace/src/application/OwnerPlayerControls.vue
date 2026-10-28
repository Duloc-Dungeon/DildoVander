<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import AwButton from '../components/AwButton.vue';
import AwNotice from '../components/AwNotice.vue';
import { account, accountRequest } from '../features/identity/session';
import { isOwnerPlayer, setOwnerPlayer } from './ownerMode';

interface GroupContext {
  groups: { id: string; name: string }[];
  groupId: string | null;
  accessRevision: number;
}
const router = useRouter();
const owner = computed(() => account.value?.roles.includes('Owner') === true);
const player = computed(() => isOwnerPlayer(account.value));
const context = ref<GroupContext | null>(null);
const busy = ref(false);
const error = ref('');
// A context change remounts the player route, discarding requests for the previous group.
const emit = defineEmits<{ contextChanged: [] }>();
let generation = 0;
watch(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : null, async () => {
  const current = ++generation;
  context.value = null;
  error.value = '';
  busy.value = false;
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

async function changeMode(): Promise<void> {
  if (busy.value) return;
  const next = !player.value;
  const current = generation;
  busy.value = true;
  error.value = '';
  try {
    if (next) {
      const value = await accountRequest<GroupContext>('/app/v1/customer/groups');
      if (current !== generation) return;
      context.value = value;
    }
    setOwnerPlayer(account.value, next);
    await router.push(next ? '/play' : '/settings/admin');
  } catch {
    if (current === generation) error.value = 'Režim se nepodařilo přepnout. Zkus to znovu.';
  } finally {
    if (current === generation) busy.value = false;
  }
}

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
    await router.push('/play');
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
  <section v-if="owner" class="owner-player-controls" aria-label="Režim vlastníka">
    <div class="owner-player-mode">
      <span>Režim: <strong>{{ player ? 'Hráč' : 'Administrace' }}</strong></span>
      <AwButton :disabled="busy" @click="changeMode">{{ player ? 'Přejít do administrace' : 'Hrát jako hráč' }}</AwButton>
    </div>
    <label v-if="player && context?.groups.length">Skupina hráčů
      <select :value="context.groupId ?? ''" :disabled="busy" @change="changeGroup">
        <option v-if="!context.groupId" value="" disabled>Vyber skupinu</option>
        <option v-for="group in context.groups" :key="group.id" :value="group.id">{{ group.name }}</option>
      </select>
    </label>
    <p v-else-if="player && context">Zatím tu není žádná skupina. Vytvoř ji v administraci večera.</p>
    <RouterLink v-if="player" to="/me/character">Moje postava a hůlka</RouterLink>
    <AwNotice v-if="error" tone="danger">{{ error }}</AwNotice>
  </section>
</template>

<style scoped>
.owner-player-controls { display: grid; gap: .75rem; margin-bottom: 1rem; padding: 1rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); background: var(--aw-surface); }
.owner-player-mode { display: flex; align-items: center; justify-content: space-between; gap: .75rem; flex-wrap: wrap; }
label { display: grid; gap: .35rem; }
select { width: 100%; min-height: 2.75rem; }
</style>
