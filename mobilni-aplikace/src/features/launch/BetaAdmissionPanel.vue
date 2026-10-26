<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest } from '../identity/session';

interface LaunchGroup {
  id: string;
  name: string;
  launchAt: number | null;
  revision: number;
  countdownDurationSeconds?: number | null;
  forceStartAtEnd?: boolean;
  countdownConfirmedAt?: number | null;
  openedAt?: number | null;
}
interface Admission { revision: number; groupId: string | null; groupName?: string | null; recruitmentOpen: boolean; fixedUniverse: string | null }
interface WaitingMember {
  userId: string;
  displayName: string;
  joinedAt: number;
  groupId: string | null;
  groupName: string | null;
  state: 'waiting' | 'revoked';
  accessRevision: number;
  fixedUniverse: string | null;
}
const admission = ref<Admission | null>(null);
const groups = ref<LaunchGroup[]>([]);
const members = ref<WaitingMember[]>([]);
const assignmentTargets = ref<Record<string, string>>({});
const selectedGroup = ref('');
const managementGroupId = ref('');
const fixedUniverse = ref('');
const recruitmentOpen = ref(false);
const durationMinutes = ref(10);
const forceStartAtEnd = ref(false);
const currentTime = ref(Date.now());
let clockTimer: ReturnType<typeof setInterval> | undefined;
const busy = ref(false);
const loading = ref(true);
const mutationBlocked = ref(false);
const error = ref('');
const message = ref('');
const currentGroup = computed(() => groups.value.find(group => group.id === admission.value?.groupId) ?? null);
const managedGroup = computed(() => groups.value.find(group => group.id === managementGroupId.value) ?? null);
const countdownDraftChanged = computed(() => managedGroup.value != null
  && (durationMinutes.value * 60 !== (managedGroup.value.countdownDurationSeconds ?? 600)
    || forceStartAtEnd.value !== (managedGroup.value.forceStartAtEnd === true)));
const groupName = computed(() => admission.value?.groupName || currentGroup.value?.name || '');
const groupLaunchLabel = computed(() => currentGroup.value?.launchAt
  ? new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short' }).format(currentGroup.value.launchAt)
  : 'brzy, termín zatím není potvrzený');
const countdownStatus = computed(() => {
  if (!managedGroup.value) return 'Vyberte skupinu.';
  if (managedGroup.value.openedAt) return 'Noc už je otevřená.';
  if (managedGroup.value.launchAt == null) return managedGroup.value.countdownDurationSeconds
    ? 'Návrh je uložený. Účastníkům se stále zobrazuje Brzy.' : 'Není nastavený odpočet. Účastníkům se zobrazuje Brzy.';
  const expired = managedGroup.value.launchAt <= currentTime.value;
  if (expired && !managedGroup.value.forceStartAtEnd) return 'Odpočet skončil. Účastníkům se stále zobrazuje Brzy, noc můžete otevřít ručně.';
  const remainingSeconds = Math.ceil(Math.max(0, managedGroup.value.launchAt - currentTime.value) / 1000);
  const days = Math.floor(remainingSeconds / 86400);
  const hours = Math.floor(remainingSeconds / 3600) % 24;
  const minutes = Math.floor(remainingSeconds / 60) % 60;
  const seconds = remainingSeconds % 60;
  const remaining = `${days ? `${days} d ` : ''}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  return `Odpočet běží: ${remaining} · konec ${new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short' }).format(managedGroup.value.launchAt)}${managedGroup.value.forceStartAtEnd ? ' · po skončení se noc otevře automaticky' : ''}.`;
});

async function loadWaitingRoom(): Promise<WaitingMember[]> {
  const roster = new Map<string, WaitingMember>();
  const seenCursors = new Set<string>();
  let cursor: string | null = null;
  do {
    const query: string = cursor === null ? '' : `?after=${encodeURIComponent(cursor)}`;
    const page = await accountRequest<{ members: WaitingMember[]; nextCursor?: string | null }>(
      `/app/v1/admin/launch/waiting-room${query}`,
    );
    if (!Array.isArray(page?.members)) throw new Error('Invalid waiting-room page');
    for (const member of page.members) roster.set(member.userId, member);
    cursor = page.nextCursor ?? null;
    if (cursor !== null) {
      if (seenCursors.has(cursor)) throw new Error('Repeated waiting-room cursor');
      seenCursors.add(cursor);
    }
  } while (cursor !== null);
  return [...roster.values()];
}

async function load(): Promise<boolean> {
  loading.value = true;
  error.value = '';
  try {
    const [current, groupList, waitingRoom] = await Promise.all([
      accountRequest<Admission>('/app/v1/admin/launch/beta-admission'),
      accountRequest<{ groups: LaunchGroup[] }>('/app/v1/admin/launch/groups'),
      loadWaitingRoom(),
    ]);
    admission.value = current;
    groups.value = Array.isArray(groupList?.groups) ? groupList.groups : [];
    members.value = waitingRoom;
    selectedGroup.value = current.groupId ?? '';
    // This management selection is intentionally independent of the recruitment default.
    if (!groups.value.some(group => group.id === managementGroupId.value)) {
      managementGroupId.value = current.groupId ?? groups.value[0]?.id ?? '';
    }
    fixedUniverse.value = current.fixedUniverse ?? '';
    recruitmentOpen.value = current.recruitmentOpen === true;
    durationMinutes.value = Math.max(1, Math.round((managedGroup.value?.countdownDurationSeconds ?? 600) / 60));
    forceStartAtEnd.value = managedGroup.value?.forceStartAtEnd === true;
    assignmentTargets.value = Object.fromEntries(members.value.map(member => [member.userId, member.groupId ?? '']));
    mutationBlocked.value = false;
    return true;
  } catch {
    error.value = 'Čekárnu a nastavení skupin se nepodařilo načíst. Obnovte stav před další změnou.';
    mutationBlocked.value = true;
    return false;
  } finally { loading.value = false; }
}

async function reconcile(failureMessage: string): Promise<void> {
  error.value = `${failureMessage} Ověřuji aktuální stav…`;
  if (await load()) error.value = `${failureMessage} Aktuální stav byl znovu načten.`;
  else error.value = 'Aktuální stav se nepodařilo ověřit. Obnovte ho před další změnou.';
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
  } catch { await reconcile('Změnu náboru se nepodařilo potvrdit.'); }
  finally { busy.value = false; }
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
  } catch { await reconcile('Skupinu se nepodařilo potvrdit.'); }
  finally { busy.value = false; }
}
async function setMemberAccess(member: WaitingMember): Promise<void> {
  if (!member.groupId || busy.value) return;
  const active = member.state === 'revoked';
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/groups/${encodeURIComponent(member.groupId)}/members/${encodeURIComponent(member.userId)}`, 'PUT', {
      expectedRevision: member.accessRevision, active,
    }, undefined, active ? `Obnovit přístup účastníkovi ${member.displayName}?` : `Odebrat přístup účastníkovi ${member.displayName}?\nNebude moct pokračovat v zážitku. Účet ani jeho data se nesmažou.`);
    message.value = active ? `Přístup pro ${member.displayName} byl obnoven.` : `Přístup pro ${member.displayName} byl odebrán.`;
    await load();
  } catch { await reconcile('Změnu přístupu se nepodařilo potvrdit.'); }
  finally { busy.value = false; }
}
async function assignGroup(member: WaitingMember): Promise<void> {
  if (busy.value) return;
  const targetGroupId = assignmentTargets.value[member.userId] || null;
  if (targetGroupId === member.groupId) return;
  const targetName = groups.value.find(group => group.id === targetGroupId)?.name ?? 'bez skupiny';
  const targetGroup = groups.value.find(group => group.id === targetGroupId);
  const mayEnterImmediately = member.state !== 'revoked' && (targetGroup?.openedAt != null
    || (targetGroup?.forceStartAtEnd === true && targetGroup.launchAt != null && targetGroup.launchAt <= Date.now()));
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/waiting-room/${encodeURIComponent(member.userId)}/group`, 'PUT', {
      expectedMembershipRevision: member.accessRevision,
      expectedSourceGroupId: member.groupId,
      targetGroupId,
    }, undefined, `Přesunout ${member.displayName} do skupiny ${targetName}?${mayEnterImmediately ? '\nTato skupina už je otevřená; účastník může vstoupit okamžitě.' : ''}${member.state === 'revoked' ? '\nPřístup zůstane odebraný.' : ''}\nÚčet a jeho data zůstanou zachované.`);
    message.value = `${member.displayName} byl přesunut do skupiny ${targetName}.${member.state === 'revoked' ? ' Přístup zůstává odebraný.' : ''}`;
    await load();
  } catch { await reconcile('Přesun účastníka se nepodařilo potvrdit.'); }
  finally { busy.value = false; }
}
async function saveCountdownDraft(): Promise<void> {
  const group = managedGroup.value;
  if (!group || busy.value || !Number.isInteger(durationMinutes.value) || durationMinutes.value < 1 || durationMinutes.value > 10080) return;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/groups/${encodeURIComponent(group.id)}/countdown`, 'PUT', {
      expectedRevision: group.revision,
      durationSeconds: durationMinutes.value * 60,
      forceStartAtEnd: forceStartAtEnd.value,
    });
    message.value = 'Návrh odpočtu je uložený. Odpočet začne až po potvrzení.';
    await load();
  } catch { await reconcile('Návrh odpočtu se nepodařilo uložit.'); }
  finally { busy.value = false; }
}
async function confirmCountdown(): Promise<void> {
  const group = managedGroup.value;
  if (!group || busy.value || durationMinutes.value < 1 || durationMinutes.value > 10080) return;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/groups/${encodeURIComponent(group.id)}/countdown/confirm`, 'POST', {
      expectedRevision: group.revision,
    }, undefined, `Spustit odpočet pro ${group.name} na ${durationMinutes.value} minut?${forceStartAtEnd.value ? '\nPo skončení se noc automaticky otevře.' : '\nPo skončení zůstane čekárna zavřená.'}`);
    message.value = 'Odpočet byl potvrzen a spuštěn.';
    await load();
  } catch { await reconcile('Spuštění odpočtu se nepodařilo potvrdit.'); }
  finally { busy.value = false; }
}
async function startNight(): Promise<void> {
  const group = managedGroup.value;
  if (!group || busy.value) return;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/groups/${encodeURIComponent(group.id)}/start`, 'POST', {
      expectedRevision: group.revision,
    }, undefined, `Otevřít noc pro skupinu ${group.name}?`);
    message.value = `Noc pro skupinu ${group.name} je otevřená.`;
    await load();
  } catch { await reconcile('Otevření noci se nepodařilo potvrdit.'); }
  finally { busy.value = false; }
}
function joinedDate(value: number): string {
  return new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium' }).format(value * 1000);
}
onMounted(() => {
  void load();
  clockTimer = setInterval(() => { currentTime.value = Date.now(); }, 1000);
});
onBeforeUnmount(() => { if (clockTimer) clearInterval(clockTimer); });
</script>

<template>
  <section class="aw-card beta-admission" aria-labelledby="beta-admission-title">
    <h3 id="beta-admission-title">Čekárna a beta skupiny</h3>
    <p>Účastníci čekající na otevření noci jsou zobrazeni napříč všemi skupinami. Změna výchozí skupiny ovlivňuje jen nové přijetí.</p>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AwButton v-if="mutationBlocked" variant="secondary" :disabled="busy" @click="load">Obnovit stav</AwButton>
    <AwButton v-else variant="secondary" :disabled="busy || loading" @click="load">Obnovit čekárnu</AwButton>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="loading" role="status">Načítání čekárny…</p>
    <template v-else-if="admission">
      <section class="waiting-room__section" aria-labelledby="waiting-room-title">
        <h4 id="waiting-room-title">Účastníci v čekárně</h4>
        <p v-if="!members.length">Čekárna je zatím prázdná.</p>
        <ul v-else class="beta-admission__members">
          <li v-for="member in members" :key="member.userId">
            <div class="waiting-room__identity"><strong>{{ member.displayName }}</strong><small>Účet {{ member.userId.slice(0, 8) }} · přijat {{ joinedDate(member.joinedAt) }}<template v-if="member.fixedUniverse"> · {{ member.fixedUniverse }}</template></small><span>{{ member.groupName || 'Bez skupiny' }} · {{ member.state === 'waiting' ? 'Čeká' : 'Přístup odebrán' }}</span></div>
            <div class="waiting-room__actions">
              <label :for="`member-group-${member.userId}`">Skupina</label>
              <select :id="`member-group-${member.userId}`" v-model="assignmentTargets[member.userId]" class="aw-select" :disabled="busy || mutationBlocked" :aria-label="`Skupina pro ${member.displayName}`">
                <option v-if="!member.groupId" value="">Bez skupiny</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option>
              </select>
              <AwButton :disabled="busy || mutationBlocked || !(assignmentTargets[member.userId] || '') || (assignmentTargets[member.userId] || '') === (member.groupId || '')" @click="assignGroup(member)">Přesunout</AwButton>
              <AwButton v-if="member.state === 'revoked' && member.groupId" variant="secondary" :disabled="busy || mutationBlocked" @click="setMemberAccess(member)">Obnovit přístup</AwButton>
              <span v-else-if="member.state === 'revoked'">Nejdříve přiřaďte skupinu, potom samostatně obnovte přístup.</span>
              <AwButton v-if="member.state === 'waiting' && member.groupId" variant="danger" :disabled="busy || mutationBlocked" @click="setMemberAccess(member)">Odebrat přístup</AwButton>
            </div>
          </li>
        </ul>
      </section>

      <section class="waiting-room__section" aria-labelledby="group-countdown-title">
        <h4 id="group-countdown-title">Odpočet skupiny</h4>
        <label for="management-group">Spravovaná skupina</label>
        <select id="management-group" v-model="managementGroupId" class="aw-select" :disabled="busy || mutationBlocked" @change="durationMinutes = Math.max(1, Math.round((managedGroup?.countdownDurationSeconds ?? 600) / 60)); forceStartAtEnd = managedGroup?.forceStartAtEnd === true">
          <option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option>
        </select>
        <p v-if="managedGroup" role="status">{{ countdownStatus }}</p>
        <template v-if="managedGroup && !managedGroup.openedAt">
          <label for="countdown-minutes">Délka odpočtu v minutách</label>
          <input id="countdown-minutes" v-model.number="durationMinutes" class="aw-input" type="number" min="1" max="10080" step="1" :disabled="busy || mutationBlocked || managedGroup.launchAt !== null" />
          <label class="waiting-room__force"><input v-model="forceStartAtEnd" type="checkbox" :disabled="busy || mutationBlocked || managedGroup.launchAt !== null" /> Spustit večer automaticky po skončení odpočtu</label>
          <p>Uložení návrhu odpočet nespustí. Účastníkům se až do potvrzení zobrazuje „Brzy“.</p>
          <AwButton v-if="managedGroup.launchAt === null" variant="secondary" :disabled="busy || mutationBlocked || !Number.isInteger(durationMinutes) || durationMinutes < 1 || durationMinutes > 10080" @click="saveCountdownDraft">Uložit návrh odpočtu</AwButton>
          <p v-if="countdownDraftChanged">Uložte změny návrhu před potvrzením odpočtu.</p>
          <AwButton v-if="managedGroup.launchAt === null" :disabled="busy || mutationBlocked || countdownDraftChanged || !Number.isInteger(durationMinutes) || durationMinutes < 1 || durationMinutes > 10080" @click="confirmCountdown">Potvrdit a spustit odpočet</AwButton>
          <AwButton v-if="managedGroup.launchAt !== null && managedGroup.launchAt <= currentTime && !managedGroup.forceStartAtEnd" :disabled="busy || mutationBlocked" @click="startNight">Spustit noc</AwButton>
        </template>
      </section>

      <section class="waiting-room__section" aria-labelledby="beta-admission-config-title">
        <h4 id="beta-admission-config-title">Nábor nových účastníků</h4>
        <p v-if="admission.groupId"><strong>{{ groupName }}</strong> · {{ groupLaunchLabel }}</p>
        <p v-else>Výchozí beta skupina zatím není nastavena. Nové přijetí zůstává uzavřené.</p>
        <div class="beta-admission__group-controls">
          <label for="beta-admission-group">Výchozí skupina pro nové přijetí</label>
          <select id="beta-admission-group" v-model="selectedGroup" class="aw-select"><option value="">Vyberte skupinu</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option></select>
          <AwButton :disabled="busy || mutationBlocked || !selectedGroup || selectedGroup === admission.groupId" @click="chooseDefault(selectedGroup)">Nastavit jako výchozí</AwButton>
          <AwButton variant="secondary" :disabled="busy || mutationBlocked || !!admission.groupId" @click="chooseDefault()">Vytvořit Interní beta</AwButton>
        </div>
        <fieldset class="beta-admission__settings" :disabled="busy || mutationBlocked || !admission.groupId">
          <legend>Nové přijetí</legend>
          <label><input v-model="recruitmentOpen" type="checkbox" /> Přijímat nové účastníky</label>
          <label for="beta-fixed-universe">Pevné univerzum <span>(prázdné = účastník si zvolí vlastní)</span></label>
          <input id="beta-fixed-universe" v-model="fixedUniverse" class="aw-input" maxlength="160" />
          <AwButton :busy="busy" @click="saveConfiguration">Uložit přístup a univerzum</AwButton>
        </fieldset>
      </section>
    </template>
  </section>
</template>

<style scoped>
.beta-admission { display: grid; gap: 1rem; }
.beta-admission h3, .beta-admission h4, .beta-admission p { margin: 0; }
.waiting-room__section { display: grid; gap: .75rem; border-top: 1px solid var(--aw-border-subtle); padding-top: 1rem; }
.beta-admission__group-controls, .beta-admission__settings { display: grid; gap: .75rem; }
.beta-admission__settings { border: 1px solid var(--aw-border-subtle); border-radius: .65rem; padding: 1rem; }
.beta-admission__settings legend { font-weight: 650; padding-inline: .4rem; }
.beta-admission__settings label { min-height: 44px; display: flex; align-items: center; gap: .5rem; }
.beta-admission__settings label span { color: var(--aw-muted); font-size: .85rem; }
.beta-admission__settings input[type="checkbox"], .waiting-room__force input { width: 20px; height: 20px; }
.waiting-room__force { min-height: 44px; display: flex; align-items: center; gap: .65rem; }
.beta-admission__members { list-style: none; padding: 0; margin: 0; }
.beta-admission__members li { display: grid; gap: .75rem; border-top: 1px solid var(--aw-border-subtle); padding-block: 1rem; }
.waiting-room__identity, .waiting-room__actions { display: grid; gap: .4rem; min-width: 0; }
.waiting-room__identity small { overflow-wrap: anywhere; color: var(--aw-muted); }
.waiting-room__actions label { font-size: .875rem; }
@media (min-width: 760px) {
  .beta-admission__members li { grid-template-columns: minmax(0, 1fr) minmax(14rem, .8fr); align-items: center; }
  .waiting-room__actions { grid-template-columns: auto minmax(8rem, 1fr) auto; align-items: center; }
}
</style>
