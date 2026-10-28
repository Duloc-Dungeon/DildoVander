<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import AccountDeletion from './AccountDeletion.vue';
import RequestDiagnostics from './RequestDiagnostics.vue';
import PushNotifications from './PushNotifications.vue';
import CustomerDataPanel from '../customer/CustomerDataPanel.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { isCustomerAccount } from '../../application/access';
import { account, accountDeletionRequested, accountRequest, refreshIdentity, authenticatePasskey, signOut, hasRole, type AccountDetails, type AccountSession } from './session';
const props = withDefaults(defineProps<{ customerSurface?: boolean }>(), { customerSurface: false });
const showCustomerData = computed(() => props.customerSurface && isCustomerAccount(account.value) && !accountDeletionRequested.value);
const details = ref<AccountDetails | null>(null);
const keyNames = ref<Record<string, string>>({});
const error = ref<CustomerCopyKey | ''>(''); const message = ref<CustomerCopyKey | ''>(''); const busy = ref(false);
const dateLocale = computed(() => customerCopy.language.value === 'en' ? 'en-GB' : 'cs-CZ');
function errorKey(value: unknown): CustomerCopyKey {
  return value instanceof DOMException && value.name === 'NotAllowedError'
    ? 'settings.error-cancelled'
    : 'settings.error-generic';
}
let detailsGeneration = 0;
watch(() => account.value?.id, () => { ++detailsGeneration; details.value = null; keyNames.value = {}; error.value = ''; message.value = ''; busy.value = false; }, { flush: 'sync' });
async function load() {
  const beforeRefresh = detailsGeneration;
  await refreshIdentity();
  if (beforeRefresh !== detailsGeneration) return;
  const generation = ++detailsGeneration;
  const accountId = account.value?.id;
  const result = account.value ? await accountRequest<AccountDetails>('/app/v1/account') : null;
  if (generation === detailsGeneration && accountId === account.value?.id) {
    details.value = result;
    keyNames.value = Object.fromEntries((result?.passkeys ?? []).map(key => [key.id, key.name ?? '']));
  }
}
onMounted(async () => { try { await load(); } catch (e) { error.value = errorKey(e); } });
async function action(work: () => Promise<unknown>, success: CustomerCopyKey) {
  const generation = detailsGeneration; const accountId = account.value?.id;
  busy.value = true; error.value = ''; message.value = '';
  try { await work(); if (generation !== detailsGeneration || accountId !== account.value?.id) return; await load(); if (accountId === account.value?.id) message.value = success; }
  catch (e) { if (generation === detailsGeneration && accountId === account.value?.id) error.value = errorKey(e); }
  finally { if (accountId === account.value?.id) busy.value = false; }
}
function trust(session: AccountSession) {
  void action(() => accountRequest(`/app/v1/installations/${encodeURIComponent(session.id)}`, 'PUT', {
    name: session.installationName, trusted: !session.trusted,
  }), 'settings.trust-changed');
}
</script>
<template>
  <section class="page-stack" :aria-label="customerSurface ? customerCopy.text('settings.customer-aria') : undefined" :aria-labelledby="customerSurface ? undefined : 'settings-title'">
    <header v-if="!customerSurface"><p class="eyebrow">{{ customerCopy.text('settings.eyebrow') }}</p><h1 id="settings-title">{{ customerCopy.text('settings.title') }}</h1><p>{{ customerCopy.text('settings.intro') }}</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ customerCopy.text(error) }}</AwNotice><AwNotice v-if="message" tone="success" role="status">{{ customerCopy.text(message) }}</AwNotice>
    <section v-if="!account" class="aw-card"><h2>{{ customerCopy.text('settings.signin-heading') }}</h2><p>{{ customerCopy.text('settings.signin-intro') }}</p><RouterLink to="/signin" class="aw-button aw-button--primary">{{ customerCopy.text('settings.signin-link') }}</RouterLink></section>
    <template v-else>
      <section v-if="customerSurface && isCustomerAccount(account) && account.roles.includes('Owner')" class="aw-card owner-administration-entry">
        <h2>Administrace večera</h2>
        <p>Připrav skupinu, pozvánky a otevření večera.</p>
        <div class="button-row">
          <RouterLink to="/settings/admin/launch" class="aw-button aw-button--primary">Večer a pozvánky</RouterLink>
          <RouterLink to="/settings/admin" class="aw-button aw-button--secondary">Účty a oprávnění</RouterLink>
        </div>
      </section>
      <PushNotifications v-if="!accountDeletionRequested" />
      <section class="aw-card"><h2>{{ account.displayName }}</h2><p v-if="details">{{ customerCopy.text('settings.passkey-count', { count: details.passkeyCount }) }}</p><AwButton :busy="busy" @click="action(() => authenticatePasskey('add'), 'settings.passkey-added')">{{ customerCopy.text('settings.add-passkey') }}</AwButton><p v-if="!customerSurface && hasRole('Administrator')"><RouterLink to="/settings/admin/launch">Večer a pozvánky</RouterLink></p></section>
      <section v-if="details" class="aw-card">
        <h2>{{ customerCopy.text('settings.passkeys') }}</h2>
        <p>{{ customerCopy.text('settings.passkey-name-help') }}</p>
        <p>{{ customerCopy.text('settings.passkey-removal-warning') }}</p>
        <p>{{ customerCopy.text('settings.passkey-reauth') }} <RouterLink :to="`/signin?return=${customerSurface ? '/me/account' : '/settings'}`">{{ customerCopy.text('settings.reauthenticate-account') }}</RouterLink></p>
        <ul class="identity-list">
          <li v-for="(key, index) in details.passkeys" :key="key.id">
            <strong>{{ key.name || customerCopy.text('settings.passkey-default-name', { number: index + 1 }) }}</strong>
            <p>{{ customerCopy.text('settings.passkey-added-at', { date: new Date(key.createdAt).toLocaleDateString(dateLocale) }) }} · {{ customerCopy.text(key.isBackedUp ? 'settings.passkey-backed-up' : 'settings.passkey-backup-unknown') }}</p>
            <form class="key-name-form" @submit.prevent="action(() => accountRequest(`/app/v1/passkeys/${encodeURIComponent(key.id)}`, 'PUT', {name: keyNames[key.id]}), 'settings.passkey-name-saved')">
              <label :for="`passkey-name-${index}`">{{ customerCopy.text('settings.passkey-name-label', { number: index + 1 }) }}</label>
              <input :id="`passkey-name-${index}`" v-model="keyNames[key.id]" class="aw-input" maxlength="80" required :disabled="busy" />
              <AwButton type="submit" :disabled="busy || !keyNames[key.id]?.trim() || keyNames[key.id]?.trim() === key.name">{{ customerCopy.text('settings.save-passkey-name') }}</AwButton>
            </form>
            <AwButton variant="danger" :disabled="busy || details.passkeys.length === 1" @click="action(() => accountRequest(`/app/v1/passkeys/${encodeURIComponent(key.id)}`, 'DELETE'), 'settings.passkey-removed')">{{ customerCopy.text('settings.remove-passkey') }}</AwButton>
          </li>
        </ul>
      </section>
      <section v-if="details" class="aw-card">
        <h2>{{ customerCopy.text('settings.sessions') }}</h2>
        <p>{{ customerCopy.text('settings.session-removal-help') }}</p>
        <ul class="identity-list">
          <li v-for="session in details.sessions" :key="session.id">
            <div>
              <strong>{{ session.installationName }}</strong>
              <span v-if="session.current"> · {{ customerCopy.text('settings.this-device') }}</span>
              <p>{{ customerCopy.text('settings.session-expires', { date: new Date(session.expiresAt * 1000).toLocaleString(dateLocale) }) }} · {{ customerCopy.text(session.trusted ? 'settings.trusted-session' : 'settings.regular-session') }}</p>
            </div>
            <div class="button-row">
              <AwButton :disabled="busy" @click="trust(session)">{{ customerCopy.text(session.trusted ? 'settings.untrust-installation' : 'settings.trust-installation') }}</AwButton>
              <AwButton variant="danger" :disabled="busy" @click="action(() => session.current ? signOut() : accountRequest(`/app/v1/sessions/${encodeURIComponent(session.id)}`, 'DELETE'), 'settings.device-removed')">{{ customerCopy.text(session.current ? 'settings.sign-out-current' : 'settings.sign-out-device') }}</AwButton>
            </div>
          </li>
        </ul>
      </section>
      <CustomerDataPanel v-if="showCustomerData" :account-id="account.id" :eligible="showCustomerData" />
    </template>
    <AccountDeletion />
    <RequestDiagnostics />
  </section>
</template>
<style scoped>
.identity-list { list-style: none; padding: 0; }.identity-list li { padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }.button-row { display: flex; flex-wrap: wrap; gap: .75rem; }.key-name-form { display: grid; gap: .5rem; margin-block: 1rem; }.key-name-form button { justify-self: start; }select { display: block; margin-top: .5rem; max-width: 100%; }
</style>
