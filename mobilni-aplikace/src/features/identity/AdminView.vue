<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import InvitationGroupFields from './InvitationGroupFields.vue';
import OperationsPanel from './OperationsPanel.vue';
import ModelReleasesPanel from './ModelReleasesPanel.vue';
import ActorVoicesPanel from './ActorVoicesPanel.vue';
import AdminNavigation from './AdminNavigation.vue';
import AdminConfirmation from './AdminConfirmation.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountOrigin, accountRequest, authenticatePasskey, refreshIdentity, hasRole, type Account, type ResourceGrant } from './session';
interface ManagedAccount extends Account { disabled: boolean }
const users = ref<ManagedAccount[]>([]); const grants = ref<ResourceGrant[]>([]);
const error = ref(''); const busy = ref(false); const inviteRole = ref('Viewer'); const token = ref('');
const message = ref(''); const failureCode = ref(''); const failureStatus = ref<number | null>(null);
let epoch = 0;
function showError(cause: unknown): void {
  error.value = cause instanceof Error ? cause.message : 'Akci účtu se nepodařilo dokončit.';
  const detail = cause as { code?: unknown; status?: unknown } | null;
  failureCode.value = typeof detail?.code === 'string' && /^[a-z][a-z0-9_]{0,79}$/.test(detail.code) ? detail.code : '';
  failureStatus.value = typeof detail?.status === 'number' && detail.status >= 400 && detail.status <= 599 ? detail.status : null;
}
const selectedUser = ref(''); const resourceType = ref('device'); const resourceId = ref(''); const permission = ref('read');
const roles = ['Owner', 'Administrator', 'Operator', 'Viewer', 'Customer', 'Beta', 'Tester'];
const roleLabels: Record<string, string> = { Owner: 'Vlastník', Administrator: 'Správce', Operator: 'Obsluha', Viewer: 'Pozorovatel', Customer: 'Účastník', Beta: 'Beta účastník', Tester: 'Tester' };
const launchGroupId = ref('');
const invitationLink = computed(() => token.value && accountOrigin.value ? `${accountOrigin.value}/signin#invite=${encodeURIComponent(token.value)}` : '');
async function load() {
  const current = epoch;
  await refreshIdentity();
  if (current !== epoch || !hasRole('Administrator')) return;
  const data = await accountRequest<{ users: ManagedAccount[]; grants: ResourceGrant[] }>('/app/v1/admin/users');
  if (current !== epoch) return;
  users.value = data.users; grants.value = data.grants;
}
onMounted(async () => { const current = epoch; try { await load(); } catch (e) { if (current === epoch) showError(e); } });
watch(() => `${account.value?.id}:${account.value?.roles.join(',')}`, () => {
  epoch++; token.value = ''; users.value = []; grants.value = []; error.value = ''; message.value = '';
  inviteRole.value = 'Viewer'; launchGroupId.value = ''; selectedUser.value = ''; resourceId.value = '';
  failureCode.value = ''; failureStatus.value = null; busy.value = false;
}, { flush: 'sync' });
onBeforeUnmount(() => { epoch++; token.value = ''; });
async function action(work: () => Promise<unknown>, reload = true) {
  if (busy.value) return;
  const current = epoch;
  busy.value = true; error.value = ''; message.value = ''; failureCode.value = ''; failureStatus.value = null;
  try {
    await work();
    if (current !== epoch) return;
    if (reload) {
      try { await load(); }
      catch { if (current === epoch) message.value = 'Změna byla potvrzena serverem, ale seznam se nepodařilo obnovit. Obnovte stránku; změnu neopakujte.'; }
    }
  } catch (e) { if (current === epoch) showError(e); }
  finally { if (current === epoch) busy.value = false; }
}
async function invite() {
  if (busy.value) return;
  const current = epoch;
  token.value = '';
  if (!launchGroupId.value) { error.value = 'Vyberte skupinu pozvaných a její termín otevření.'; return; }
  await action(async () => {
    const result = await accountRequest<{ token: string; purpose: string }>('/app/v1/admin/invitations', 'POST', { role: inviteRole.value, launchGroupId: launchGroupId.value });
    if (current !== epoch) return;
    if (typeof result?.token !== 'string' || !/^[A-Za-z0-9_-]{20,256}$/.test(result.token)
      || result.purpose !== 'invite') throw new Error('Server nevrátil platnou pozvánku. Vytvoření nebylo potvrzeno.');
    token.value = result.token;
    message.value = 'Jednorázový odkaz byl vytvořen. Předejte ho soukromě účastníkovi.';
  }, false);
}
async function reauthenticate() {
  const current = epoch;
  await action(async () => { await authenticatePasskey('reauth'); if (current === epoch) message.value = 'Totožnost byla ověřena. Původní akci nyní odešlete znovu.'; }, false);
}
</script>
<template>
  <section class="page-stack admin-accounts" aria-labelledby="admin-title">
    <header><h1 id="admin-title">Uživatelé a oprávnění</h1><p>Vlastník spravuje účty a pozvánky. Správci přidělují přístup ke konkrétním prostředkům.</p><p class="admin-aside">Večer se tváří jako spontánní. Tady se mu to pečlivě plánuje.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}
      <p v-if="failureStatus || failureCode">{{ failureStatus ? `HTTP ${failureStatus}` : '' }}{{ failureStatus && failureCode ? ' · ' : '' }}{{ failureCode }}</p>
      <AwButton v-if="failureCode === 'recent_passkey_required'" :busy="busy" @click="reauthenticate">Ověřit totožnost přístupovým klíčem</AwButton>
    </AwNotice>
    <AdminConfirmation v-if="message && token">{{ message }}</AdminConfirmation>
    <AwNotice v-else-if="message" tone="info" role="status">{{ message }}</AwNotice>
    <AwNotice v-if="!hasRole('Administrator')" tone="warning">Tato stránka vyžaduje oprávnění správce. <RouterLink to="/settings">Zpět na účet</RouterLink></AwNotice>
    <template v-else>
      <AdminNavigation current="accounts" />
      <p><RouterLink :to="{path:'/signin',query:{return:'/settings/admin'}}">Znovu ověřit účet přístupovým klíčem</RouterLink></p>
      <OperationsPanel />
      <ModelReleasesPanel />
      <ActorVoicesPanel />
      <section class="aw-card"><h2>Postavy a portréty</h2><p>Seznam postav, úpravy jejich údajů a nahrání náhradního obrázku.</p><RouterLink class="aw-button" to="/settings/admin/characters">Spravovat postavy</RouterLink></section>
      <section class="aw-card"><h2>Řízení spuštění</h2><p>Náborová skupina, spuštění aplikace, čekárna a příprava portrétů.</p><RouterLink class="aw-button aw-button--primary" to="/settings/admin/launch">Otevřít správu spuštění</RouterLink></section>
      <section v-if="hasRole('Owner')" id="invitations" class="aw-card"><h2>Pozvánka</h2><p>Pozvánka přidá účastníka do vybrané skupiny. <RouterLink to="/settings/admin/launch#waiting-room">Nastavit skupinu a spuštění aplikace</RouterLink></p><form class="identity-form" @submit.prevent="invite"><label for="invite-role">Role nového účtu</label><select id="invite-role" v-model="inviteRole" class="aw-select"><option v-for="role in roles" :key="role" :value="role">{{ roleLabels[role] }}</option></select><InvitationGroupFields v-model="launchGroupId" :disabled="busy" /><AwButton type="submit" :busy="busy">Vytvořit pozvánku</AwButton></form><div v-if="token" class="invite-output"><p>Kód platí jednu hodinu a lze ho použít jednou. Předávejte ho soukromě. Znovu ho nelze zobrazit.</p><label v-if="invitationLink" for="invitation-link">Soukromý odkaz pro přijetí</label><input v-if="invitationLink" id="invitation-link" :value="invitationLink" readonly class="aw-input" autocomplete="off" /><label for="invitation-token">Jednorázový kód</label><input id="invitation-token" :value="token" readonly class="aw-input" autocomplete="off" /><AwButton @click="token = ''">Skrýt kód</AwButton></div></section>
      <section class="aw-card"><h2>Účty</h2><p v-if="!users.length">Žádné účty k zobrazení.</p><ul class="identity-list"><li v-for="user in users" :key="user.id"><h3>{{ user.displayName }} <small v-if="user.id === account?.id">· váš účet</small></h3><form v-if="hasRole('Owner')" class="user-controls" @submit.prevent="action(() => accountRequest(`/app/v1/admin/users/${encodeURIComponent(user.id)}`, 'PUT', { role: user.roles[0], disabled: user.disabled }))"><label :for="`role-${user.id}`">Role</label><select :id="`role-${user.id}`" v-model="user.roles[0]" class="aw-select"><option v-for="role in roles" :key="role" :value="role">{{ roleLabels[role] }}</option></select><label><input v-model="user.disabled" type="checkbox" /> Účet je zablokován</label><AwButton type="submit" :disabled="busy">Uložit a odhlásit relace</AwButton></form><p v-else>{{ user.roles.map(role => roleLabels[role] ?? role).join(', ') }} · {{ user.disabled ? 'zablokován' : 'aktivní' }}</p></li></ul></section>
      <section class="aw-card"><h2>Přidělit prostředek</h2><form class="identity-form" @submit.prevent="action(() => accountRequest('/app/v1/admin/grants', 'POST', { userId: selectedUser, resourceType, resourceId, permission }))"><label for="grant-user">Uživatel</label><select id="grant-user" v-model="selectedUser" required class="aw-select"><option value="" disabled>Vyberte účet</option><option v-for="user in users.filter(u => !u.disabled)" :key="user.id" :value="user.id">{{ user.displayName }}</option></select><label for="grant-type">Typ prostředku</label><select id="grant-type" v-model="resourceType" class="aw-select"><option value="device">Kufr</option><option value="workshop">Dílna</option></select><label for="grant-id">Identifikátor prostředku</label><input id="grant-id" v-model="resourceId" required maxlength="128" class="aw-input" /><label for="grant-permission">Oprávnění</label><select id="grant-permission" v-model="permission" class="aw-select"><option value="read">Prohlížet</option><option value="operate">Ovládat</option><option value="manage">Spravovat</option></select><AwButton type="submit" :busy="busy">Přidělit</AwButton></form><ul class="identity-list"><li v-for="grant in grants" :key="grant.id"><p>{{ users.find(u => u.id === grant.userId)?.displayName ?? grant.userId }} · {{ grant.resourceType }} · {{ grant.resourceId }} · {{ grant.permission }}</p><AwButton variant="danger" :disabled="busy" @click="action(() => accountRequest(`/app/v1/admin/grants/${encodeURIComponent(grant.id)}`, 'DELETE'))">Odebrat oprávnění</AwButton></li></ul></section>
    </template>
  </section>
</template>
<style scoped>
.admin-accounts { container: admin-accounts / inline-size; max-width: 74rem; margin-inline: auto; }
.admin-accounts > header { max-width: 68ch; }
.admin-aside { max-width: 48ch; color: var(--wns-violet); }
.identity-form { display: grid; gap: .75rem; max-width: 36rem; }
.identity-form > label,
.user-controls > label:first-child { color: var(--aw-muted); font-weight: 700; }
.identity-list { list-style: none; padding: 0; margin-block: 1rem 0; }
.identity-list li { min-width: 0; border-top: 1px solid var(--aw-border-subtle); padding-block: 1rem; }
.identity-list h3 { margin-block: 0 .75rem; overflow-wrap: anywhere; }
.identity-list p { overflow-wrap: anywhere; }
.user-controls { display: grid; grid-template-columns: auto minmax(10rem, 1fr) auto auto; align-items: center; gap: .75rem; }
.user-controls .aw-button { min-height: 44px; }
.user-controls > label:has(input[type='checkbox']) { display: flex; align-items: center; gap: .5rem; min-height: 44px; }
.invite-output { margin-top: 1.5rem; display: grid; gap: .75rem; max-width: 42rem; }
.invite-output input { min-width: 0; }
small { font-size: .85rem; }
@media (max-width: 760px) {
  .user-controls { grid-template-columns: minmax(0, 1fr); align-items: stretch; }
  .user-controls .aw-select,
  .user-controls .aw-button { width: 100%; }
}
@container admin-accounts (max-width: 48rem) {
  .user-controls { grid-template-columns: minmax(0, 1fr); align-items: stretch; }
  .user-controls .aw-select,
  .user-controls .aw-button { width: 100%; }
}
</style>
