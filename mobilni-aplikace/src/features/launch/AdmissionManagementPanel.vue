<script setup lang="ts">
import GameBossControl from '../game/GameBossControl.vue';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import AdminReauthentication from '../identity/AdminReauthentication.vue';
import { account, accountRequest } from '../identity/session';
import { formatLaunchDate, launchTimeZone, parseLaunchDate } from './launchDate';

interface LaunchGroup {
  id: string;
  name: string;
  launchAt: number | null;
  revision: number;
  openedAt?: number | null;
  chronicleClosedAt?: number | null;
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
const openingDate = ref('');
const selectedRecruitmentGroup = computed(() => groups.value.find(group => group.id === selectedGroup.value));
watch(selectedGroup, () => { openingDate.value = formatLaunchDate(selectedRecruitmentGroup.value?.launchAt ?? null); });
const managementGroupId = ref('');
const fixedUniverse = ref('');
const recruitmentOpen = ref(false);
const currentTime = ref(Date.now());
let clockTimer: ReturnType<typeof setInterval> | undefined;
const busy = ref(false);
const loading = ref(true);
const mutationBlocked = ref(false);
const error = ref('');
const reauthenticationRequired = ref(false);
const message = ref('');
const lastActionSection = ref<'members' | 'admission' | null>(null);
const membersDisclosure = ref<HTMLDetailsElement>();
const admissionDisclosure = ref<HTMLElement>();
const currentGroup = computed(() => groups.value.find(group => group.id === admission.value?.groupId) ?? null);
const managedGroup = computed(() => groups.value.find(group => group.id === managementGroupId.value) ?? null);
const managedWaitingCount = computed(() => members.value.filter(member =>
  member.groupId === managedGroup.value?.id && member.state === 'waiting').length);
const waitingMemberCount = computed(() => members.value.filter(member => member.state === 'waiting').length);
const invitationStatus = computed(() => {
  if (!admission.value?.groupId) return 'Uzavřeno · skupina není nastavena';
  return admission.value.recruitmentOpen ? 'Otevřené' : 'Uzavřené';
});
const groupName = computed(() => admission.value?.groupName || currentGroup.value?.name || '');
const groupLaunchLabel = computed(() => currentGroup.value?.openedAt ? 'Večer je otevřený' : currentGroup.value?.launchAt
  ? new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short', timeZone: launchTimeZone }).format(currentGroup.value.launchAt)
  : 'Termín otevření není nastavený');
const countdownStatus = computed(() => {
  if (!managedGroup.value) return 'Vyberte skupinu.';
  if (managedGroup.value.openedAt) return 'Noc už je otevřená.';
  if (managedGroup.value.launchAt == null) return 'Termín otevření této skupiny není nastavený.';
  const expired = managedGroup.value.launchAt <= currentTime.value;
  if (expired) return 'Termín uplynul. Obnovte stav otevření.';
  const remainingSeconds = Math.ceil(Math.max(0, managedGroup.value.launchAt - currentTime.value) / 1000);
  const days = Math.floor(remainingSeconds / 86400);
  const hours = Math.floor(remainingSeconds / 3600) % 24;
  const minutes = Math.floor(remainingSeconds / 60) % 60;
  const seconds = remainingSeconds % 60;
  const remaining = `${days ? `${days} d ` : ''}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  return `Do otevření skupiny: ${remaining} · otevření ${new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short', timeZone: launchTimeZone }).format(managedGroup.value.launchAt)}.`;
});

watch([error, reauthenticationRequired], ([currentError, needsReauthentication]) => {
  if (!currentError && !needsReauthentication) return;
  if (lastActionSection.value === 'members' && membersDisclosure.value) membersDisclosure.value.open = true;
  if (lastActionSection.value === 'admission') admissionDisclosure.value?.scrollIntoView?.({ block: 'nearest' });
}, { flush: 'post' });

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
    openingDate.value = formatLaunchDate(groups.value.find(group => group.id === current.groupId)?.launchAt ?? null);
    // This management selection is intentionally independent of the recruitment default.
    if (!groups.value.some(group => group.id === managementGroupId.value)) {
      managementGroupId.value = current.groupId ?? groups.value[0]?.id ?? '';
    }
    fixedUniverse.value = current.fixedUniverse ?? '';
    recruitmentOpen.value = current.recruitmentOpen === true;
    assignmentTargets.value = Object.fromEntries(members.value.map(member => [member.userId, member.groupId ?? '']));
    mutationBlocked.value = false;
    return true;
  } catch {
    error.value = 'Čekárnu a nastavení skupin se nepodařilo načíst. Obnovte stav před další změnou.';
    mutationBlocked.value = true;
    return false;
  } finally { loading.value = false; }
}

async function reconcile(failureMessage: string, cause: unknown): Promise<void> {
  reauthenticationRequired.value = (cause as { code?: string } | null)?.code === 'recent_passkey_required';
  if (reauthenticationRequired.value) {
    error.value = cause instanceof Error ? cause.message : 'Nejprve znovu ověřte svou totožnost přístupovým klíčem.';
    return;
  }
  error.value = `${failureMessage} Ověřuji aktuální stav…`;
  if (await load()) error.value = `${failureMessage} Aktuální stav byl znovu načten.`;
  else error.value = 'Aktuální stav se nepodařilo ověřit. Obnovte ho před další změnou.';
}
async function saveConfiguration(): Promise<void> {
  if (!admission.value || busy.value || mutationBlocked.value) return;
  lastActionSection.value = 'admission';
  const group = selectedRecruitmentGroup.value;
  if (!group) { error.value = 'Vyberte skupinu pro nové hosty.'; return; }
  // An open group's disabled date is display-only; preserve its exact historical timestamp.
  const launchAt = group.openedAt != null ? group.launchAt
    : openingDate.value ? parseLaunchDate(openingDate.value) : null;
  if (openingDate.value && launchAt == null) {
    error.value = 'Zadaný čas v pásmu Europe/Prague neexistuje. Zkontrolujte datum a změnu letního času.';
    return;
  }
  if (recruitmentOpen.value && launchAt == null && group.openedAt == null) {
    error.value = 'Před otevřením náboru nastavte termín spuštění aplikace.';
    return;
  }
  busy.value = true; error.value = ''; message.value = '';
  try {
    admission.value = await accountRequest<Admission>('/app/v1/admin/launch/beta-admission', 'PUT', {
      expectedRevision: admission.value.revision,
      groupId: group.id,
      expectedGroupRevision: group.revision,
      launchAt,
      recruitmentOpen: recruitmentOpen.value,
      fixedUniverse: fixedUniverse.value.trim() || null,
    });
    await load();
    message.value = 'Skupina, termín spuštění a nábor jsou uložené.';
  } catch (cause) { await reconcile('Změnu náboru se nepodařilo potvrdit.', cause); }
  finally { busy.value = false; }
}
async function createDefaultGroup(): Promise<void> {
  if (busy.value) return;
  lastActionSection.value = 'admission';
  busy.value = true; error.value = ''; message.value = '';
  try {
    const result = await accountRequest<Admission>('/app/v1/admin/launch/beta-admission/group', 'POST', {
      expectedRevision: admission.value?.revision,
    });
    if (!result || typeof result.revision !== 'number') throw new Error('unconfirmed');
    message.value = 'Skupina pro nové hosty byla vytvořena. Nastavte termín spuštění a otevřete nábor.';
    await load();
  } catch (cause) { await reconcile('Skupinu se nepodařilo potvrdit.', cause); }
  finally { busy.value = false; }
}
async function setMemberAccess(member: WaitingMember): Promise<void> {
  if (!member.groupId || busy.value) return;
  lastActionSection.value = 'members';
  const active = member.state === 'revoked';
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/groups/${encodeURIComponent(member.groupId)}/members/${encodeURIComponent(member.userId)}`, 'PUT', {
      expectedRevision: member.accessRevision, active,
    }, undefined, active ? `Obnovit přístup účastníkovi ${member.displayName}?` : `Odebrat přístup účastníkovi ${member.displayName}?\nNebude moct pokračovat v zážitku. Účet ani jeho data se nesmažou.`);
    message.value = active ? `Přístup pro ${member.displayName} byl obnoven.` : `Přístup pro ${member.displayName} byl odebrán.`;
    await load();
  } catch (cause) { await reconcile('Změnu přístupu se nepodařilo potvrdit.', cause); }
  finally { busy.value = false; }
}
async function assignGroup(member: WaitingMember): Promise<void> {
  if (busy.value) return;
  const targetGroupId = assignmentTargets.value[member.userId] || null;
  if (targetGroupId === member.groupId) return;
  lastActionSection.value = 'members';
  const targetName = groups.value.find(group => group.id === targetGroupId)?.name ?? 'bez skupiny';
  const targetGroup = groups.value.find(group => group.id === targetGroupId);
  const mayEnterImmediately = member.state !== 'revoked' && targetGroup?.openedAt != null;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/waiting-room/${encodeURIComponent(member.userId)}/group`, 'PUT', {
      expectedMembershipRevision: member.accessRevision,
      expectedSourceGroupId: member.groupId,
      targetGroupId,
    }, undefined, `Přesunout ${member.displayName} do skupiny ${targetName}?${mayEnterImmediately ? '\nTato skupina už je otevřená; účastník může vstoupit okamžitě.' : ''}${member.state === 'revoked' ? '\nPřístup zůstane odebraný.' : ''}\nÚčet a jeho data zůstanou zachované.`);
    message.value = `${member.displayName} byl přesunut do skupiny ${targetName}.${member.state === 'revoked' ? ' Přístup zůstává odebraný.' : ''}`;
    await load();
  } catch (cause) { await reconcile('Přesun účastníka se nepodařilo potvrdit.', cause); }
  finally { busy.value = false; }
}
async function closeChronicle(): Promise<void> {
  const group = managedGroup.value;
  if (!group?.openedAt || group.chronicleClosedAt || busy.value) return;
  lastActionSection.value = null;
  busy.value = true; error.value = ''; message.value = '';
  try {
    const response = await accountRequest<{ closedAt: number }>(`/app/v1/admin/launch/groups/${encodeURIComponent(group.id)}/chronicle-close`, 'POST', {
      expectedRevision: group.revision,
    }, undefined, `Uložit závěr večera pro skupinu ${group.name}? Kronika použije jen už odhalené vzpomínky.`);
    if (!Number.isSafeInteger(response.closedAt) || response.closedAt <= 0) throw new Error('Server nevrátil platný čas uložení.');
    message.value = `Závěr večera pro skupinu ${group.name} byl uložen.`;
    await load();
  } catch (cause) { await reconcile('Uložení závěru večera se nepodařilo potvrdit.', cause); }
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
  <section id="waiting-room" class="aw-card admission-management" aria-labelledby="admission-management-title">
    <h3 id="admission-management-title">Pozvaní a otevření večera</h3>
    <p>Noví hosté se při registraci zařadí do náborové skupiny. Do jejího otevření čekají v čekárně; dříve otevřené skupiny pokračují dál.</p>
    <section class="evening-readiness" aria-label="Připravenost večera">
      <h4>Připravenost</h4>
      <dl>
        <div><dt>Spravovaná skupina</dt><dd>{{ mutationBlocked ? 'Stav není ověřený' : loading ? 'Načítám stav…' : managedGroup?.name ?? (groups.length ? 'Vyberte skupinu' : 'Skupina není dostupná') }}</dd></div>
        <div><dt>Čekající hosté</dt><dd>{{ mutationBlocked ? 'Stav není ověřený' : loading || !managedGroup ? 'Stav není načtený' : managedWaitingCount }}</dd></div>
        <div><dt>Přijímání hostů</dt><dd>{{ mutationBlocked ? 'Stav není ověřený' : loading || !admission ? 'Stav není načtený' : `${invitationStatus}${groupName ? ` · ${groupName}` : ''}` }}</dd></div>
        <div><dt>Otevření</dt><dd>{{ mutationBlocked ? 'Stav není ověřený' : loading || !managedGroup ? 'Stav není načtený' : countdownStatus }}</dd></div>
      </dl>
    </section>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AdminReauthentication v-if="reauthenticationRequired" @verified="reauthenticationRequired = false; error = ''; message = 'Totožnost byla ověřena. Původní změnu nyní odešlete znovu.'" />
    <AwButton v-if="mutationBlocked" variant="secondary" :disabled="busy" @click="load">Obnovit stav</AwButton>
    <AwButton v-else variant="secondary" :disabled="busy || loading" @click="load">Obnovit čekárnu</AwButton>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="loading" role="status">Načítání čekárny…</p>
    <template v-else-if="admission">
      <section ref="admissionDisclosure" class="waiting-room__section recruitment-settings" aria-labelledby="admission-management-settings-title">
        <h4 id="admission-management-settings-title">Nábor a spuštění aplikace</h4>
        <p v-if="admission.groupId">Aktuální náborová skupina: <strong>{{ groupName }}</strong> · {{ groupLaunchLabel }}</p>
        <p v-else>Náborová skupina zatím není nastavená.</p>
        <form class="admission-management__settings" @submit.prevent="saveConfiguration">
          <fieldset :disabled="busy || mutationBlocked">
            <legend>Noví hosté</legend>
            <label for="admission-management-group">Skupina pro nově připojené hosty</label>
            <select id="admission-management-group" v-model="selectedGroup" class="aw-select" required><option value="">Vyberte skupinu</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option></select>
            <AwButton v-if="!groups.length" variant="secondary" :disabled="busy || mutationBlocked" @click="createDefaultGroup">Vytvořit skupinu pro nové hosty</AwButton>
            <label for="admission-launch">Kdy se spustí aplikace pro tuto skupinu</label>
            <input id="admission-launch" v-model="openingDate" class="aw-input" type="datetime-local" :disabled="!selectedRecruitmentGroup || selectedRecruitmentGroup.openedAt != null" :required="recruitmentOpen && selectedRecruitmentGroup?.openedAt == null" aria-describedby="admission-launch-help" />
            <p id="admission-launch-help">Časové pásmo: {{ launchTimeZone }}. Noví hosté počkají do tohoto termínu. Změna náborové skupiny nepřesouvá stávající účastníky.</p>
            <AwNotice v-if="selectedRecruitmentGroup?.openedAt != null" tone="info">Tato skupina už je otevřená. Noví hosté v ní vstoupí ihned. Pro další čekárnu vyberte dosud neotevřenou skupinu.</AwNotice>
            <label><input v-model="recruitmentOpen" type="checkbox" /> Přijímat nové účastníky</label>
            <label for="admission-fixed-universe">Pevné univerzum <span>(prázdné = účastník si zvolí vlastní)</span></label>
            <input id="admission-fixed-universe" v-model="fixedUniverse" class="aw-input" maxlength="100" />
            <AwButton type="submit" :busy="busy" :disabled="!selectedRecruitmentGroup">Uložit skupinu, spuštění a nábor</AwButton>
          </fieldset>
        </form>
        <RouterLink to="/settings/admin#invitations">Spravovat skupiny a pozvánky</RouterLink>
      </section>
      <section class="waiting-room__section" aria-labelledby="group-opening-title">
        <h4 id="group-opening-title">Skupina a otevření</h4>
        <label for="management-group">Spravovaná skupina</label>
        <select id="management-group" v-model="managementGroupId" class="aw-select" :disabled="busy || mutationBlocked">
          <option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option>
        </select>
        <p v-if="managedGroup" role="status">{{ countdownStatus }}</p>
        <template v-if="managedGroup?.openedAt">
          <p>Uloží se závěrečná kronika z už odhalených vzpomínek. Dosud neodhalené záznamy zůstanou soukromé.</p>
          <p v-if="managedGroup.chronicleClosedAt" role="status">Závěr večera je uložený {{ joinedDate(managedGroup.chronicleClosedAt) }}.</p>
          <AwButton v-else :disabled="busy || mutationBlocked || loading" @click="closeChronicle">Uložit závěr večera</AwButton>
        </template>
        <GameBossControl v-if="managedGroup?.openedAt" :key="managedGroup.id" :group-id="managedGroup.id" />
      </section>

      <details ref="membersDisclosure" class="waiting-room__disclosure" aria-labelledby="waiting-room-title">
        <summary id="waiting-room-title">Účastníci v čekárně ({{ waitingMemberCount }} čekají)</summary>
        <section class="waiting-room__section" aria-label="Účastníci v čekárně">
        <p v-if="!members.length">Čekárna je zatím prázdná.</p>
        <ul v-else class="admission-management__members">
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
      </details>


    </template>
  </section>
</template>

<style scoped>
.admission-management { display: grid; gap: 1rem; }
.admission-management h3, .admission-management h4, .admission-management p { margin: 0; }
.waiting-room__section { display: grid; gap: .75rem; border-top: 1px solid var(--aw-border-subtle); padding-top: 1rem; }
.evening-readiness { display: grid; gap: .5rem; padding-bottom: 1rem; border-bottom: 1px solid var(--aw-border-subtle); }
.evening-readiness h4, .evening-readiness dl, .evening-readiness p { margin: 0; }
.evening-readiness dl { display: grid; gap: .65rem; grid-template-columns: repeat(auto-fit, minmax(min(100%, 10rem), 1fr)); }
.evening-readiness dl > div { min-width: 0; display: grid; gap: .2rem; }
.evening-readiness dt { color: var(--aw-muted); font-size: .8rem; }
.evening-readiness dd { margin: 0; font-weight: 650; overflow-wrap: anywhere; }
.waiting-room__disclosure { border-top: 1px solid var(--aw-border-subtle); padding-top: 1rem; }
.waiting-room__disclosure > summary { min-height: 44px; display: list-item; list-style-position: inside; padding-block: .65rem; cursor: pointer; font-weight: 650; }
.waiting-room__disclosure[open] > section { margin-top: .75rem; }
.admission-management__group-controls, .admission-management__settings { display: grid; gap: .75rem; }
.admission-management__settings { border: 1px solid var(--aw-border-subtle); border-radius: .65rem; padding: 1rem; }
.admission-management__settings legend { font-weight: 650; padding-inline: .4rem; }
.admission-management__settings label { min-height: 44px; display: flex; align-items: center; gap: .5rem; }
.admission-management__settings label span { color: var(--aw-muted); font-size: .85rem; }
.admission-management__settings input[type="checkbox"] { width: 20px; height: 20px; }

.admission-management__members { list-style: none; padding: 0; margin: 0; }
.admission-management__members li { display: grid; gap: .75rem; border-top: 1px solid var(--aw-border-subtle); padding-block: 1rem; }
.waiting-room__identity, .waiting-room__actions { display: grid; gap: .4rem; min-width: 0; }
.waiting-room__identity small { overflow-wrap: anywhere; color: var(--aw-muted); }
.waiting-room__actions label { font-size: .875rem; }
@media (min-width: 760px) {
  .admission-management__members li { grid-template-columns: minmax(0, 1fr) minmax(14rem, .8fr); align-items: center; }
  .waiting-room__actions { grid-template-columns: auto minmax(8rem, 1fr) auto; align-items: center; }
}
</style>
