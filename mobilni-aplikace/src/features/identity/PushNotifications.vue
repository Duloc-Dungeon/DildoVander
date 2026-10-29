<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, accountRequest } from './session';
import { pausePushReconnection, resumePushReconnection, serializePushMutation } from '../../platform/pushCoordinator';
import { nativePushConfigured, pushPlatform, readPushPermission, registerPush, requestPushPermission, supportsPush, unregisterLocalPush, type PushPermission, type PushSettings } from '../../platform/pushNotifications';
import { dismissPushPrompt, isPushPromptDismissed, pushSettingsRevision } from './pushPrompt';

const props = defineProps<{ context?: 'waiting' | 'wand' | 'game' }>();
const titleId = `push-title-${useId()}`;

const supported = supportsPush();
const settings = ref<PushSettings | null>(null);
const busy = ref(false);
const error = ref('');
const message = ref('');
const permission = ref(false);
const permissionState = ref<PushPermission>('prompt');
const localConfiguration = ref(true);
const available = computed(() => supported && localConfiguration.value && settings.value?.[pushPlatform()] === true);
const enabled = computed(() => settings.value?.enabled === true && permission.value);
const offerVisible = computed(() => !!account.value && !accountDeletionRequested.value && available.value
  && settings.value?.enabled === false && !isPushPromptDismissed(account.value.id)
  && (permissionState.value !== 'denied' || !!error.value));
const offerTitle = computed(() => props.context === 'waiting' ? 'Dáme ti vědět, až večer začne.'
  : props.context === 'wand' ? 'Hůlku máš. Další výzvu ti připomeneme.' : 'Ať ti neuteče další výzva.');
let controller = new AbortController();
let mutationSignal: AbortSignal | undefined;

async function loadSettings(): Promise<void> {
  controller.abort();
  controller = new AbortController();
  const signal = controller.signal;
  settings.value = null; error.value = ''; message.value = ''; busy.value = false;
  if (!account.value || accountDeletionRequested.value || !supported) return;
  busy.value = true;
  try {
    const result = await accountRequest<PushSettings>('/app/v1/push', 'GET', undefined, signal);
    const configured = await nativePushConfigured();
    const allowed = await readPushPermission();
    if (!signal.aborted) { settings.value = result; permissionState.value = allowed; permission.value = allowed === 'granted'; localConfiguration.value = configured; }
  } catch {
    if (!signal.aborted) error.value = 'Nastavení oznámení se nepodařilo načíst. Zkus stránku otevřít znovu.';
  } finally { if (!signal.aborted) busy.value = false; }
}
watch([() => account.value?.id, accountDeletionRequested], loadSettings, { immediate: true });
watch(pushSettingsRevision, () => {
  // A foreign save must replace an older GET even while it is loading. Only
  // the instance performing its own save already has the authoritative state.
  if (mutationSignal !== controller.signal) void loadSettings();
}, { flush: 'sync' });
onBeforeUnmount(() => controller.abort());

function later(): void {
  if (account.value && !busy.value) dismissPushPrompt(account.value.id);
}

async function changeEnabled() {
  if (busy.value || !settings.value) return;
  const signal = controller.signal;
  const accountId = account.value?.id;
  const check = () => {
    if (signal.aborted || !accountId || accountId !== account.value?.id || accountDeletionRequested.value) throw new DOMException('Účet se změnil.', 'AbortError');
  };
  busy.value = true; error.value = ''; message.value = '';
  mutationSignal = signal;
  try {
    check();
    if (settings.value.enabled) {
      pausePushReconnection();
      await serializePushMutation(() => { check(); return accountRequest('/app/v1/push', 'DELETE', undefined, signal); });
      check();
      settings.value = { ...settings.value, enabled: false };
      dismissPushPrompt(accountId!);
      pushSettingsRevision.value++;
      // Server revocation takes effect even if the OS cannot unregister right now.
      await unregisterLocalPush().catch(() => {});
      check();
      message.value = 'Oznámení jsou na tomto zařízení vypnutá.';
    } else {
      const allowed = await requestPushPermission();
      check();
      permission.value = allowed;
      permissionState.value = allowed ? 'granted' : 'denied';
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
      pushSettingsRevision.value++;
      message.value = 'Oznámení jsou na tomto zařízení zapnutá.';
    }
  } catch {
    if (!signal.aborted) error.value = 'Změnu oznámení se nepodařilo uložit. Zkus to znovu.';
  } finally {
    if (mutationSignal === signal) mutationSignal = undefined;
    if (!signal.aborted) busy.value = false;
  }
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
  <section v-if="!context || offerVisible" class="aw-card page-stack" :class="{ 'push-offer': context }" :aria-labelledby="titleId" :aria-busy="busy || undefined">
    <h2 :id="titleId">{{ context ? offerTitle : 'Oznámení' }}</h2>
    <p>{{ context === 'waiting' ? 'Nemusíš hlídat odpočet. Pošleme ti oznámení, až se večer otevře, i když aplikaci zavřeš.' : 'Výzvy do hry, pozvánky a změny večera ti připomeneme, i když je aplikace zavřená.' }} Zapnutí platí pro toto zařízení a přihlášení.</p>
    <template v-if="context">
      <div class="push-actions">
        <AwButton v-if="permissionState !== 'denied'" variant="primary" :busy="busy" :disabled="busy || accountDeletionRequested" @click="changeEnabled">Zapnout oznámení</AwButton>
        <AwButton :disabled="busy" @click="later">Později</AwButton>
      </div>
    </template>
    <template v-else>
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
    </template>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AwNotice v-if="message" tone="success" role="status">{{ message }}</AwNotice>
  </section>
</template>

<style scoped>
.push-actions { display: flex; flex-wrap: wrap; gap: .75rem; }
.push-offer { border-color: var(--wns-mint, #b8f3d4); background: var(--wns-surface-raised, #222626); color: var(--wns-text, #f5f5f0); }
.push-offer h2 { margin: 0; font-family: var(--wns-display); font-size: clamp(1.2rem, 4vw, 1.5rem); line-height: 1.25; }
.push-offer p { margin: 0; line-height: 1.5; }
.push-offer :deep(.aw-button) { min-height: 48px; }
.push-offer :deep(.aw-button--primary) { color: var(--wns-on-mint, #111513); background: var(--wns-mint, #b8f3d4); }
</style>
