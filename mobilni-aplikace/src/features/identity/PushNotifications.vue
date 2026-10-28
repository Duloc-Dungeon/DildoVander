<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, accountRequest } from './session';
import { pausePushReconnection, resumePushReconnection, serializePushMutation } from '../../platform/pushCoordinator';
import { hasPushPermission, nativePushConfigured, pushPlatform, registerPush, requestPushPermission, supportsPush, unregisterLocalPush, type PushSettings } from '../../platform/pushNotifications';

const supported = supportsPush();
const settings = ref<PushSettings | null>(null);
const busy = ref(false);
const error = ref('');
const message = ref('');
const permission = ref(false);
const localConfiguration = ref(true);
const available = computed(() => supported && localConfiguration.value && settings.value?.[pushPlatform()] === true);
const enabled = computed(() => settings.value?.enabled === true && permission.value);
let controller = new AbortController();

watch([() => account.value?.id, accountDeletionRequested], async () => {
  controller.abort();
  controller = new AbortController();
  const signal = controller.signal;
  settings.value = null; error.value = ''; message.value = ''; busy.value = false;
  if (!account.value || accountDeletionRequested.value || !supported) return;
  busy.value = true;
  try {
    const result = await accountRequest<PushSettings>('/app/v1/push', 'GET', undefined, signal);
    const configured = await nativePushConfigured();
    const allowed = await hasPushPermission();
    if (!signal.aborted) { settings.value = result; permission.value = allowed; localConfiguration.value = configured; }
  } catch {
    if (!signal.aborted) error.value = 'Nastavení oznámení se nepodařilo načíst. Zkus stránku otevřít znovu.';
  } finally { if (!signal.aborted) busy.value = false; }
}, { immediate: true });
onBeforeUnmount(() => controller.abort());

async function changeEnabled() {
  if (busy.value || !settings.value) return;
  const signal = controller.signal;
  const accountId = account.value?.id;
  const check = () => {
    if (signal.aborted || !accountId || accountId !== account.value?.id || accountDeletionRequested.value) throw new DOMException('Účet se změnil.', 'AbortError');
  };
  busy.value = true; error.value = ''; message.value = '';
  try {
    check();
    if (settings.value.enabled) {
      pausePushReconnection();
      await serializePushMutation(() => { check(); return accountRequest('/app/v1/push', 'DELETE', undefined, signal); });
      check();
      settings.value = { ...settings.value, enabled: false };
      // Server revocation takes effect even if the OS cannot unregister right now.
      await unregisterLocalPush().catch(() => {});
      check();
      message.value = 'Oznámení jsou na tomto zařízení vypnutá.';
    } else {
      const allowed = await requestPushPermission();
      check();
      permission.value = allowed;
      if (!allowed) {
        error.value = 'Oznámení nejsou povolená. Povol je v nastavení prohlížeče nebo telefonu a zkus to znovu.';
        return;
      }
      const registration = await registerPush(settings.value, check);
      check();
      await serializePushMutation(() => { check(); return accountRequest('/app/v1/push', 'PUT', registration, signal); });
      check();
      settings.value = { ...settings.value, enabled: true };
      resumePushReconnection();
      message.value = 'Oznámení jsou na tomto zařízení zapnutá.';
    }
  } catch {
    if (!signal.aborted) error.value = 'Změnu oznámení se nepodařilo uložit. Zkus to znovu.';
  } finally { if (!signal.aborted) busy.value = false; }
}

async function testPush() {
  if (busy.value || !enabled.value) return;
  const signal = controller.signal;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest('/app/v1/push/test', 'POST', undefined, signal);
    if (!signal.aborted) message.value = 'Zkušební oznámení bylo zařazeno k odeslání.';
  } catch {
    if (!signal.aborted) error.value = 'Zkušební oznámení se nepodařilo odeslat. Zkus to znovu.';
  } finally { if (!signal.aborted) busy.value = false; }
}
</script>

<template>
  <section class="aw-card page-stack" aria-labelledby="push-title" :aria-busy="busy || undefined">
    <h2 id="push-title">Oznámení</h2>
    <p>Výzvy do hry, pozvánky a změny večera ti připomeneme, i když je aplikace zavřená. Zapnutí platí pro toto zařízení a přihlášení.</p>
    <p v-if="!supported">Tento prohlížeč oznámení nepodporuje. Na iPhonu nebo iPadu přidej web na plochu a otevři ho odtud, nebo použij mobilní aplikaci.</p>
    <p v-else-if="!settings && busy" role="status">Načítám nastavení oznámení…</p>
    <p v-else-if="settings && !available">Oznámení pro toto zařízení zatím nejsou připravena.</p>
    <template v-if="available || settings?.enabled">
      <p role="status">{{ enabled ? 'Oznámení jsou zapnutá.' : 'Oznámení jsou vypnutá.' }}</p>
      <p v-if="settings?.enabled && !permission">Oprávnění bylo odebráno v nastavení zařízení. Vypni oznámení a po obnovení oprávnění je zapni znovu.</p>
      <div class="push-actions">
        <AwButton :busy="busy" :disabled="accountDeletionRequested" @click="changeEnabled">{{ settings?.enabled ? 'Vypnout oznámení' : 'Zapnout oznámení' }}</AwButton>
        <AwButton v-if="enabled && available" :disabled="busy || accountDeletionRequested" @click="testPush">Poslat zkušební oznámení</AwButton>
      </div>
    </template>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AwNotice v-if="message" tone="success" role="status">{{ message }}</AwNotice>
  </section>
</template>

<style scoped>
.push-actions { display: flex; flex-wrap: wrap; gap: .75rem; }
</style>
