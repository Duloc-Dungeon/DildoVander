<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest } from '../identity/session';

interface LaunchGroup { id: string; name: string; launchAt: number | null; revision: number }
interface Admission { revision: number; groupId: string | null; groupName?: string | null; recruitmentOpen: boolean; fixedUniverse: string | null }
interface Member { userId: string; displayName: string; joinedAt: number; accessActive: boolean; accessRevision: number }
const admission = ref<Admission | null>(null);
const groups = ref<LaunchGroup[]>([]);
const members = ref<Member[]>([]);
const selectedGroup = ref('');
const fixedUniverse = ref('');
const recruitmentOpen = ref(false);
const busy = ref(false);
const loading = ref(true);
const mutationBlocked = ref(false);
const error = ref('');
const message = ref('');
const currentGroup = computed(() => groups.value.find(group => group.id === admission.value?.groupId) ?? null);
const groupName = computed(() => admission.value?.groupName || currentGroup.value?.name || '');
const groupLaunchLabel = computed(() => currentGroup.value?.launchAt
  ? new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short' }).format(currentGroup.value.launchAt)
  : 'bez termínu');

async function loadMembers(groupId: string | null): Promise<void> {
  members.value = [];
  if (!groupId) return;
  const result = await accountRequest<{ members: Member[] }>(`/app/v1/admin/launch/groups/${encodeURIComponent(groupId)}/members`);
  members.value = Array.isArray(result?.members) ? result.members : [];
}

async function load(): Promise<boolean> {
  loading.value = true;
  error.value = '';
  try {
    const [current, groupList] = await Promise.all([
      accountRequest<Admission>('/app/v1/admin/launch/beta-admission'),
      accountRequest<{ groups: LaunchGroup[] }>('/app/v1/admin/launch/groups'),
    ]);
    admission.value = current;
    groups.value = Array.isArray(groupList?.groups) ? groupList.groups : [];
    selectedGroup.value = current.groupId ?? '';
    fixedUniverse.value = current.fixedUniverse ?? '';
    recruitmentOpen.value = current.recruitmentOpen === true;
    await loadMembers(current.groupId);
    mutationBlocked.value = false;
    return true;
  } catch {
    error.value = 'Nastavení beta skupiny se nepodařilo načíst. Obnovte stránku a ověřte uložený stav.';
    mutationBlocked.value = true;
    return false;
  } finally { loading.value = false; }
}

async function saveConfiguration(): Promise<void> {
  if (!admission.value || busy.value) return;
  busy.value = true; error.value = ''; message.value = '';
  try {
    admission.value = await accountRequest<Admission>('/app/v1/admin/launch/beta-admission', 'PUT', {
      expectedRevision: admission.value.revision,
      groupId: admission.value.groupId,
      recruitmentOpen: recruitmentOpen.value,
      fixedUniverse: fixedUniverse.value.trim() || null,
    });
    message.value = recruitmentOpen.value ? 'Nábor do výchozí skupiny je otevřený.' : 'Nábor do výchozí skupiny je uzavřený.';
    await loadMembers(admission.value.groupId);
  } catch {
    error.value = 'Změnu se nepodařilo potvrdit. Ověřuji aktuální nastavení…';
    if (!await load()) error.value = 'Stav změny se nepodařilo ověřit. Obnovte stav před další změnou.';
  } finally { busy.value = false; }
}

async function chooseDefault(groupId?: string): Promise<void> {
  if (busy.value) return;
  busy.value = true; error.value = ''; message.value = '';
  try {
    const result = await accountRequest<Admission>('/app/v1/admin/launch/beta-admission/group', 'POST', {
      expectedRevision: admission.value?.revision,
      ...(groupId ? { groupId } : {}),
    });
    if (!result || typeof result.revision !== 'number') throw new Error('unconfirmed');
    message.value = groupId ? 'Výchozí beta skupina byla změněna.' : 'Byla vytvořena skupina Interní beta. Nábor zůstává uzavřený.';
    await load();
  } catch {
    error.value = 'Skupinu se nepodařilo potvrdit. Ověřuji aktuální nastavení…';
    if (!await load()) error.value = 'Stav změny skupiny se nepodařilo ověřit. Obnovte stav před další změnou.';
  } finally { busy.value = false; }
}

async function setMemberAccess(member: Member): Promise<void> {
  if (!admission.value?.groupId || busy.value) return;
  const active = !member.accessActive;
  const name = member.displayName;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/groups/${encodeURIComponent(admission.value.groupId)}/members/${encodeURIComponent(member.userId)}`, 'PUT', {
      expectedRevision: member.accessRevision,
      active,
    }, undefined, active
      ? `Obnovit přístup účastníkovi ${name}? Účet a uložené údaje budou znovu dostupné ve stejné skupině.`
      : `Odebrat přístup účastníkovi ${name}?\nNebude moct pokračovat v zážitku. Účet ani jeho data se nesmažou.`);
    message.value = active ? 'Přístup účastníka byl obnoven.' : 'Přístup účastníka byl odebrán.';
    await loadMembers(admission.value.groupId);
  } catch {
    error.value = 'Změnu přístupu se nepodařilo potvrdit. Ověřuji aktuální stav účastníků…';
    if (!await load()) error.value = 'Stav změny přístupu se nepodařilo ověřit. Obnovte stav před další změnou.';
  } finally { busy.value = false; }
}

function joinedDate(value: number): string {
  // The API exposes account timestamps as Unix seconds.
  return new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium' }).format(value * 1000);
}
onMounted(() => { void load(); });
</script>

<template>
  <section class="aw-card beta-admission" aria-labelledby="beta-admission-title">
    <h3 id="beta-admission-title">Beta skupina a účastníci</h3>
    <p>Jedna skupina přijímá účastníky z iOS i Android aplikace. Změna výchozí skupiny nepřesouvá stávající účty.</p>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AwButton v-if="mutationBlocked" variant="secondary" :disabled="busy" @click="load">Obnovit stav</AwButton>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="loading" role="status">Načítání nastavení beta skupiny…</p>
    <template v-else-if="admission">
      <p v-if="admission.groupId"><strong>{{ groupName }}</strong> · otevření zážitku: {{ groupLaunchLabel }}</p>
      <p v-else>Výchozí beta skupina zatím není nastavena. Nové přijetí zůstává uzavřené.</p>
      <div class="beta-admission__group-controls">
        <label for="beta-admission-group">Existující skupina</label>
        <select id="beta-admission-group" v-model="selectedGroup" class="aw-select"><option value="">Vyberte skupinu</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option></select>
        <AwButton :disabled="busy || mutationBlocked || !selectedGroup || selectedGroup === admission.groupId" @click="chooseDefault(selectedGroup)">Nastavit jako výchozí</AwButton>
        <AwButton variant="secondary" :disabled="busy || mutationBlocked || !!admission.groupId" @click="chooseDefault()">Vytvořit Interní beta</AwButton>
      </div>
      <fieldset class="beta-admission__settings" :disabled="busy || mutationBlocked || !admission.groupId">
        <legend>Přijímání nových účastníků</legend>
        <label><input v-model="recruitmentOpen" type="checkbox" /> Přijímat nové účastníky</label>
        <label for="beta-fixed-universe">Pevné univerzum <span>(prázdné = účastník si zvolí vlastní)</span></label>
        <input id="beta-fixed-universe" v-model="fixedUniverse" class="aw-input" maxlength="160" />
        <AwButton :busy="busy" @click="saveConfiguration">Uložit přístup a univerzum</AwButton>
      </fieldset>
      <h4>Účastníci</h4>
      <p v-if="!admission.groupId || !members.length">Zatím nejsou evidovaní žádní účastníci.</p>
      <ul v-else class="beta-admission__members">
        <li v-for="member in members" :key="member.userId">
          <div><strong>{{ member.displayName }}</strong><small>Účet {{ member.userId.slice(0, 8) }} · přijat {{ joinedDate(member.joinedAt) }}</small><span :class="member.accessActive ? 'is-active' : 'is-revoked'">{{ member.accessActive ? 'Aktivní' : 'Přístup odebrán' }}</span></div>
          <AwButton :variant="member.accessActive ? 'danger' : 'secondary'" :disabled="busy || mutationBlocked" @click="setMemberAccess(member)">{{ member.accessActive ? 'Odebrat přístup' : 'Obnovit přístup' }}</AwButton>
        </li>
      </ul>
    </template>
  </section>
</template>

<style scoped>
.beta-admission { display: grid; gap: 1rem; }
.beta-admission h3, .beta-admission h4, .beta-admission p { margin: 0; }
.beta-admission__group-controls, .beta-admission__settings { display: grid; gap: .75rem; }
.beta-admission__settings { border: 1px solid var(--aw-border-subtle); border-radius: .65rem; padding: 1rem; }
.beta-admission__settings legend { font-weight: 650; padding-inline: .4rem; }
.beta-admission__settings label { min-height: 44px; display: flex; align-items: center; gap: .5rem; }
.beta-admission__settings label span { color: var(--aw-muted); font-size: .85rem; }
.beta-admission__settings input[type="checkbox"] { width: 20px; height: 20px; }
.beta-admission__members { list-style: none; padding: 0; margin: 0; }
.beta-admission__members li { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: .75rem; border-top: 1px solid var(--aw-border-subtle); padding-block: 1rem; }
.beta-admission__members li > div { display: grid; gap: .3rem; min-width: 0; }
.beta-admission__members small { overflow-wrap: anywhere; color: var(--aw-muted); }
.is-active { color: var(--aw-success, #6fbd9a); }
.is-revoked { color: var(--aw-danger, #d97f7a); }
</style>
