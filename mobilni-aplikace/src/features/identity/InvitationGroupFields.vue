<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest } from './session';
interface LaunchGroup { id: string; name: string; launchAt: number | null; revision: number }
const props = defineProps<{ modelValue: string; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [id: string] }>();
const groups = ref<LaunchGroup[]>([]);
const name = ref('');
const opening = ref('');
const busy = ref(false);
const error = ref('');
const notice = ref('');
const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
const selected = computed(() => groups.value.find(group => group.id === props.modelValue));
let epoch = 0;
function localInput(milliseconds: number | null): string {
  if (milliseconds === null) return '';
  const date = new Date(milliseconds);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}
function choose(id: string): void {
  emit('update:modelValue', id);
  const group = groups.value.find(value => value.id === id);
  name.value = group?.name ?? ''; opening.value = localInput(group?.launchAt ?? null); notice.value = '';
}
async function load(): Promise<void> {
  const expected = epoch;
  try {
    const result = await accountRequest<{ groups: LaunchGroup[] }>('/app/v1/admin/launch/groups');
    if (expected === epoch) groups.value = Array.isArray(result?.groups) ? result.groups : [];
  } catch { if (expected === epoch) error.value = 'Skupiny se nepodařilo načíst. Obnovte stránku před vytvořením pozvánky.'; }
}
async function save(): Promise<void> {
  if (busy.value || props.disabled) return;
  error.value = ''; notice.value = '';
  if (!name.value.trim()) { error.value = 'Zadejte název skupiny.'; return; }
  const launchAt = opening.value ? new Date(opening.value).getTime() : null;
  if (launchAt !== null && !Number.isFinite(launchAt)) { error.value = 'Zadejte platný čas otevření.'; return; }
  const expected = epoch;
  const group = selected.value;
  busy.value = true;
  try {
    const result = await accountRequest<{ group: LaunchGroup }>(group ? `/app/v1/admin/launch/groups/${encodeURIComponent(group.id)}` : '/app/v1/admin/launch/groups', group ? 'PUT' : 'POST', {
      name: name.value.trim(), launchAt, ...(group ? { expectedRevision: group.revision } : {}),
    });
    if (expected !== epoch) return;
    if (!result?.group?.id) throw new Error('group_unconfirmed');
    groups.value = [...groups.value.filter(value => value.id !== result.group.id), result.group];
    choose(result.group.id);
    notice.value = 'Termín je uložený pouze pro členy této skupiny.';
  } catch { if (expected === epoch) error.value = 'Skupinu se nepodařilo uložit. Před dalším pokusem obnovte její aktuální stav.'; }
  finally { if (expected === epoch) busy.value = false; }
}
watch(() => account.value?.id, () => { epoch++; groups.value = []; emit('update:modelValue', ''); name.value = ''; opening.value = ''; error.value = ''; notice.value = ''; busy.value = false; });
onMounted(load);
onBeforeUnmount(() => { epoch++; });
</script>
<template>
  <fieldset class="invitation-group" :disabled="disabled || busy">
    <legend>Skupina pozvaných a čas otevření</legend>
    <label for="invitation-group">Skupina pozvánky</label>
    <select id="invitation-group" class="aw-select" :value="modelValue" required @change="choose(($event.target as HTMLSelectElement).value)"><option value="">Vyberte nebo vytvořte skupinu</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option></select>
    <p>Termín platí jen pro účty vytvořené z pozvánek této skupiny. Ostatní skupiny ani globální plán se nezmění.</p>
    <details><summary>{{ selected ? 'Upravit termín nebo vytvořit další skupinu' : 'Vytvořit skupinu' }}</summary>
      <div class="group-edit">
        <AwButton v-if="selected" :disabled="busy" @click="choose('')">Nová skupina</AwButton>
        <label for="invitation-group-name">Název skupiny</label><input id="invitation-group-name" v-model="name" class="aw-input" maxlength="80" />
        <label for="invitation-group-opening">Čas otevření ({{ zone }})</label><input id="invitation-group-opening" v-model="opening" class="aw-input" type="datetime-local" />
        <p>Bez termínu zůstane skupina uzavřená. Změna uloženého termínu platí pro všechny členy této skupiny.</p>
        <AwButton :busy="busy" @click="save">{{ selected ? 'Uložit termín skupiny' : 'Vytvořit skupinu' }}</AwButton>
      </div>
    </details>
    <AwNotice v-if="error" role="alert" tone="danger">{{ error }}</AwNotice><p v-if="notice" role="status">{{ notice }}</p>
  </fieldset>
</template>
<style scoped>
.invitation-group { display: grid; gap: .75rem; border: 1px solid var(--aw-border-subtle); border-radius: 8px; padding: 1rem; min-width: 0; }
.invitation-group legend { font-weight: 600; }
.invitation-group p { color: var(--aw-muted); font-size: .875rem; margin: 0; }
.invitation-group summary { cursor: pointer; min-height: 44px; padding-block: .65rem; }
.group-edit { display: grid; gap: .75rem; }
</style>
