<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import GeneratedWand from '../archive/GeneratedWand.vue';
import WandWorkshop from '../game/WandWorkshop.vue';
import { AccountArchiveClient } from '../archive/accountArchive';
import { projectWandArtwork, useWandArtworkRetry, type WandArtwork } from '../archive/wandArtwork';
import { useArchiveMedia } from '../archive/useArchiveMedia';
import { isUuid } from '../archive/client';
import { projectGeneratedWand, type GeneratedWand as GeneratedWandSnapshot } from '../archive/generatedWand';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest } from '../identity/session';
import { isOwnerPlayer } from '../../application/ownerMode';
import CustomerKeepsakes from './CustomerKeepsakes.vue';
import SettingsView from '../identity/SettingsView.vue';
import CharacterBriefAssistant from './CharacterBriefAssistant.vue';
import CharacterLabelEditor from './CharacterLabelEditor.vue';
import { characterDisplayName } from './characterLabel';
import PrivateCharacterAvatar from './PrivateCharacterAvatar.vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { projectCustomerSessionError, type CustomerSessionFeedback } from './customerSessionFeedback';

interface CharacterProfile {
  profileId: string; revision: number; label?: string | null;
  userInput: { name: string; world: string; version?: string; description?: string; language?: string };
  status: string; enrichment?: unknown; errorCode?: string; updatedAt?: string;
}
const route = useRoute();
watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true, flush: 'sync' });
const pane = computed(() => route.path.split('/').at(-1) ?? 'character');
const profile = ref<CharacterProfile | null>(null);
const selectedWand = ref<GeneratedWandSnapshot | null>(null);
const crafting = ref(false);
watch(() => profile.value ? `${profile.value.profileId}:${profile.value.revision}` : '', () => {
  crafting.value = false;
}, { flush: 'sync' });
const profileArtworkClient = new AccountArchiveClient(() => account.value?.id ?? '');
const profileArtwork = useArchiveMedia(profileArtworkClient);
const artworkState = ref<WandArtwork | null>(null);
const artworkRetry = useWandArtworkRetry();
let profileArtworkPath = '';
function clearProfileArtwork(): void {
  profileArtworkPath = '';
  profileArtworkClient.invalidate();
  profileArtwork.clear();
}
function bindProfileArtwork(raw: unknown): void {
  artworkState.value = projectWandArtwork(raw);
  const value = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw as Record<string, unknown> : null;
  const locator = value && isUuid(value.locator) ? value.locator : null;
  const assetId = value && isUuid(value.assetId) ? value.assetId : null;
  const path = selectedWand.value && locator && assetId
    ? `/app/v1/archive/owned/${locator}/assets/${assetId}` : '';
  if (path === profileArtworkPath) return;
  clearProfileArtwork();
  profileArtworkPath = path;
  if (path) void profileArtwork.artwork(path);
}
const busy = ref(false);
const profileLoading = ref(false);
const errorFeedback = ref<CustomerSessionFeedback | null>(null);
const noticeCopyKey = ref<CustomerCopyKey | null>(null);
const canEditCharacter = computed(() => !isOwnerPlayer(account.value) && account.value?.roles.some(role => role === 'Owner' || role === 'Administrator') === true);
const hasAiConsent = computed(() => account.value?.openAiConsent === true);
const form = ref({ name: '', world: '', version: '', description: '', language: '' });
let generation = 0;
let accountWorkController = new AbortController();
let enrichmentTimer: ReturnType<typeof setTimeout> | undefined;
const paneTitleKeys: Record<string, CustomerCopyKey> = {
  character: 'customer.character-pane', keepsakes: 'customer.keepsakes-pane', account: 'customer.account-pane',
};
const profileStatusKeys: Record<string, CustomerCopyKey> = {
  saved: 'customer.status.saved', queued: 'customer.status.queued', running: 'customer.status.running', ready: 'customer.status.ready',
  needs_clarification: 'customer.status.needs_clarification', failed: 'customer.status.failed',
};
const enrichmentText = computed(() => {
  if (!profile.value?.enrichment) return '';
  if (typeof profile.value.enrichment === 'string') return profile.value.enrichment;
  const value = profile.value.enrichment as Record<string, unknown>;
  return [value.summary, value.description, value.text].filter(item => typeof item === 'string').join('\n');
});
const evidenceSections = computed(() => {
  const enrichment = profile.value?.enrichment;
  if (!enrichment || typeof enrichment !== 'object') return [];
  const source = enrichment as Record<string, unknown>;
  return (['jobs', 'evidence', 'facts', 'sources', 'conversationIdeas'] as const).flatMap(key => {
    const items = key === 'evidence' ? source.evidence : source[key];
    if (!Array.isArray(items)) return [];
    const lines = items.map(item => {
      if (typeof item === 'string') return item;
      if (!item || typeof item !== 'object') return '';
      const record = item as Record<string, unknown>;
      const text = [record.title, record.name, record.summary, record.description, record.text]
        .find(value => typeof value === 'string') as string | undefined ?? '';
      const urls = Array.isArray(record.sourceUrls) ? record.sourceUrls.filter(value => typeof value === 'string').join(' · ') : '';
      const url = typeof record.url === 'string' ? record.url : '';
      return [text, urls || url].filter(Boolean).join(' · ');
    }).filter(Boolean);
    const titleKey: CustomerCopyKey = key === 'jobs' ? 'customer.evidence-preparation'
      : key === 'sources' ? 'customer.evidence-sources'
        : key === 'conversationIdeas' ? 'customer.evidence-ideas' : 'customer.evidence-facts';
    return lines.length ? [{ title: customerCopy.text(titleKey), lines }] : [];
  });
});
const clarificationText = computed(() => {
  const enrichment = profile.value?.enrichment;
  if (!enrichment || typeof enrichment !== 'object') return '';
  const value = enrichment as Record<string, unknown>;
  if (value.needsClarification !== true || !Array.isArray(value.ambiguities)) return '';
  return value.ambiguities.filter(item => typeof item === 'string').join('\n');
});

function resetPrivateState(): void {
  clearProfileArtwork();
  artworkState.value = null;
  artworkRetry.invalidate();
  generation += 1;
  accountWorkController.abort();
  accountWorkController = new AbortController();
  if (enrichmentTimer) clearTimeout(enrichmentTimer);
  enrichmentTimer = undefined;
  profile.value = null;
  selectedWand.value = null;
  profileLoading.value = Boolean(account.value);
  busy.value = false;
  crafting.value = false;
  errorFeedback.value = null;
  noticeCopyKey.value = null;
  form.value = { name: '', world: '', version: '', description: '', language: '' };
}

async function loadProfile(epoch = generation): Promise<void> {
  if (!account.value) return;
  // Refreshing an existing profile must not dispose its active voice workshop.
  profileLoading.value = !profile.value;
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null; generatedWand?: unknown; artwork?: unknown }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation) return;
    profile.value = result.profile;
    selectedWand.value = result.profile ? projectGeneratedWand(result.generatedWand) : null;
    bindProfileArtwork(result.artwork);
    form.value = {
      name: result.profile?.userInput.name ?? '', world: result.profile?.userInput.world ?? '',
      version: result.profile?.userInput.version ?? '', description: result.profile?.userInput.description ?? '',
      language: result.profile?.userInput.language ?? '',
    };
    scheduleEnrichmentRefresh(epoch);
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) profileLoading.value = false; }
}

async function saveCharacter(): Promise<void> {
  if (busy.value || !hasAiConsent.value || (profile.value && !canEditCharacter.value)) return;
  errorFeedback.value = null; noticeCopyKey.value = null; busy.value = true; const epoch = generation;
  try {
    const input = form.value;
    const result = await accountRequest<{ profile: CharacterProfile }>('/app/v1/customer/character', 'PUT', {
      expectedRevision: profile.value?.revision ?? 0, expectedProfileId: profile.value?.profileId ?? null,
      openAiConsent: hasAiConsent.value, portraitConsent: hasAiConsent.value,
      userInput: { name: input.name.trim(), world: input.world.trim(), version: input.version.trim() || undefined,
        description: input.description.trim() || undefined, language: input.language.trim() || undefined },
    }, accountWorkController.signal);
    if (epoch !== generation) return;
    profile.value = result.profile;
    selectedWand.value = null;
    clearProfileArtwork();
    scheduleEnrichmentRefresh(epoch);
    noticeCopyKey.value = 'customer.profile-saved';
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function requestCorrection(): Promise<void> {
  if (!profile.value || !canEditCharacter.value || !hasAiConsent.value) return;
  errorFeedback.value = null; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character/correction', 'POST', {
      expectedRevision: profile.value.revision, expectedProfileId: profile.value.profileId, openAiConsent: true,
    }, accountWorkController.signal);
    if (epoch === generation) { noticeCopyKey.value = 'customer.not-my-profile'; await refreshEnrichment(epoch); }
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function deleteCharacter(): Promise<void> {
  if (!profile.value || !window.confirm(customerCopy.text('customer.delete-confirm'))) return;
  errorFeedback.value = null; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character?expectedRevision=' + profile.value.revision + '&expectedProfileId=' + encodeURIComponent(profile.value.profileId), 'DELETE', undefined, accountWorkController.signal);
    if (epoch === generation) { profile.value = null; selectedWand.value = null; form.value = { name: '', world: '', version: '', description: '', language: '' }; noticeCopyKey.value = 'customer.profile-deleted'; }
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

function scheduleEnrichmentRefresh(epoch: number): void {
  if (enrichmentTimer) clearTimeout(enrichmentTimer);
  if (profile.value && (['queued', 'running'].includes(profile.value.status) || (['queued', 'running'].includes(artworkState.value?.status ?? '') || artworkState.value?.status === 'missing' && artworkState.value.canRetry))) {
    enrichmentTimer = setTimeout(() => { void refreshEnrichment(epoch); }, 3000);
  }
}
async function refreshEnrichment(epoch = generation): Promise<void> {
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null; generatedWand?: unknown; artwork?: unknown }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation || !result.profile || result.profile.profileId !== profile.value?.profileId || result.profile.revision !== profile.value.revision) return;
    selectedWand.value = projectGeneratedWand(result.generatedWand);
    bindProfileArtwork(result.artwork);
    // Refresh server-derived enrichment only; never replace the visitor's in-progress form edits.
    profile.value = { ...profile.value, status: result.profile.status, enrichment: result.profile.enrichment, errorCode: result.profile.errorCode, updatedAt: result.profile.updatedAt };
    scheduleEnrichmentRefresh(epoch);
  } catch { if (epoch === generation) scheduleEnrichmentRefresh(epoch); }
}

function showProjectedError(cause: unknown): void {
  errorFeedback.value = projectCustomerSessionError(cause);
}

watch(() => account.value?.id, (next, previous) => {
  if (next !== previous) {
    resetPrivateState();
    if (next) void loadProfile(generation);
  }
}, { flush: 'sync' });
onMounted(() => {
  if (account.value) void loadProfile(generation);
});
onBeforeUnmount(resetPrivateState);
</script>

<template>
  <section class="page-stack customer-page" :aria-labelledby="`customer-${pane}-title`">
    <header><p class="eyebrow">{{ customerCopy.text('customer.space') }}</p><h1 :id="`customer-${pane}-title`">{{ customerCopy.text(paneTitleKeys[pane] ?? 'customer.space') }}</h1><p v-if="pane === 'character'">{{ customerCopy.text('customer.character-header') }}</p></header>
    <AwNotice v-if="errorFeedback" tone="warning" role="alert">{{ errorFeedback.copyKey ? customerCopy.text(errorFeedback.copyKey) : errorFeedback.message }}<small v-if="errorFeedback.diagnosticCode"> {{ customerCopy.text('customer.diagnostic', { code: errorFeedback.diagnosticCode }) }}</small></AwNotice>
    <AwNotice v-if="noticeCopyKey" tone="info" role="status">{{ customerCopy.text(noticeCopyKey) }}</AwNotice>

    <p v-if="pane === 'character' && profileLoading" role="status">{{ customerCopy.text('customer.profile-loading') }}</p>
    <section v-if="pane === 'character' && !profileLoading" class="page-stack">
      <PrivateCharacterAvatar v-if="profile" :profile-id="profile.profileId" :revision="profile.revision" :name="profile.userInput.name" />
      <WandWorkshop v-if="profile && (!selectedWand || crafting)" :key="`${profile.profileId}:${profile.revision}`" @active-change="crafting = $event" @created="loadProfile()" />
      <GeneratedWand v-if="profile && selectedWand" :wand="selectedWand" show-name
        :image-url="profileArtwork.artworkUrl.value"
        :image-pending="!!profileArtworkPath && !profileArtwork.artworkUrl.value && !profileArtwork.artworkError.value"
        :image-error="profileArtwork.artworkError.value" :image-state="artworkState?.status" :synthetic="artworkState?.synthetic"
        :can-retry="artworkState?.canRetry" :retry-busy="artworkRetry.busy.value" @retry="artworkRetry.retry(artworkState, () => refreshEnrichment())"
        :keepsake-to="artworkState ? `/keepsake/?account=1&k=${artworkState.locator}` : undefined" history-to="/me/keepsakes" />
      <p v-if="artworkRetry.failed.value" role="alert">{{ customerCopy.language.value === 'en' ? 'The retry could not be confirmed. Refresh the state before trying again.' : 'Opakování se nepodařilo potvrdit. Před dalším pokusem obnov stav.' }}</p>
      <section v-if="profile" class="aw-card page-stack" aria-labelledby="character-confirmation-title">
        <h2 id="character-confirmation-title">{{ characterDisplayName(profile.userInput.name, profile.label) }}</h2>
        <CharacterLabelEditor :profile-id="profile.profileId" :label="profile.label" @saved="profile.label = $event" />
        <p>{{ profile.userInput.world }}<span v-if="profile.userInput.version"> · {{ profile.userInput.version }}</span></p>
        <p role="status">{{ customerCopy.text(profileStatusKeys[profile.status] ?? 'customer.status-unknown') }} · {{ customerCopy.text('customer.revision', { revision: profile.revision }) }}</p>
        <p v-if="profile.status === 'saved'">{{ customerCopy.text('customer.saved-guidance') }}</p>
        <AwNotice v-if="clarificationText" tone="info">{{ customerCopy.text('customer.clarification', { text: clarificationText }) }}</AwNotice>
        <RouterLink v-if="['saved', 'ready'].includes(profile.status)" class="aw-button aw-button--primary" to="/play/wand">Otevřít dílnu</RouterLink><RouterLink v-if="selectedWand" to="/play">Přejít ke hrám</RouterLink>
        <p v-if="!canEditCharacter" class="aw-muted">Na účtu máš jednu postavu. Její úpravy řeší administrátor.</p>
      </section>
      <details v-if="!profile || canEditCharacter" :open="!profile || profile.status === 'needs_clarification'" class="aw-card">
        <summary>{{ customerCopy.text(profile ? 'customer.edit-character' : 'customer.create-first') }}</summary>
    <form class="customer-form" @submit.prevent="saveCharacter">
      <AwField id="character-name" :label="customerCopy.text('profile.name')"><input id="character-name" v-model="form.name" class="aw-input" maxlength="120" required /></AwField>
      <AwField id="character-world" :label="customerCopy.text('profile.world')"><input id="character-world" v-model="form.world" class="aw-input" maxlength="160" required /></AwField>
      <details class="character-extra" :open="!!profile">
        <summary>Doplnit postavu <span class="aw-muted">(nepovinné)</span></summary>
        <div class="character-extra-fields">
          <AwField id="character-version" :label="customerCopy.text('profile.version')"><input id="character-version" v-model="form.version" class="aw-input" maxlength="120" /></AwField>
          <AwField id="character-description" :label="customerCopy.text('profile.description')"><textarea id="character-description" v-model="form.description" class="aw-input" maxlength="1200" rows="4" /></AwField>
          <CharacterBriefAssistant v-model="form.description" :disabled="busy" />
          <AwField id="character-language" label="Jazyk postavy (nepovinné)"><input id="character-language" v-model="form.language" class="aw-input" maxlength="35" /></AwField>
        </div>
      </details>
      <p v-if="profile" class="aw-muted">{{ customerCopy.text('customer.preparation-status') }}: {{ customerCopy.text(profileStatusKeys[profile.status] ?? 'customer.status-unknown') }}</p>
      <AwNotice v-if="profile && clarificationText" tone="info">{{ customerCopy.text('customer.clarification', { text: clarificationText }) }}</AwNotice>
      <details v-if="enrichmentText || evidenceSections.length">
        <summary>{{ customerCopy.text('customer.preparation-details') }}</summary>
        <p v-if="enrichmentText" class="character-enrichment">{{ enrichmentText }}</p>
        <section v-for="section in evidenceSections" :key="section.title" class="aw-card"><h2>{{ section.title }}</h2><ul><li v-for="line in section.lines" :key="line">{{ line }}</li></ul></section>
      </details>
      <div class="customer-actions"><AwButton type="submit" variant="primary" :busy="busy" :disabled="!hasAiConsent">{{ customerCopy.text(profile ? 'customer.save-changes' : 'customer.create-character') }}</AwButton><AwButton v-if="profile" type="button" :disabled="busy || !hasAiConsent" @click="requestCorrection">{{ customerCopy.text('customer.not-my-character') }}</AwButton><AwButton v-if="profile" type="button" variant="danger" :disabled="busy" @click="deleteCharacter">{{ customerCopy.text('customer.delete-profile') }}</AwButton></div>
      <p v-if="profile" class="aw-muted">{{ customerCopy.text('customer.revision', { revision: profile.revision }) }}</p>
    </form>
      </details>
      <RouterLink to="/me/account">{{ customerCopy.text('customer.account-help') }}</RouterLink>
    </section>

    <CustomerKeepsakes v-else-if="pane === 'keepsakes'" />
    <section v-else-if="pane === 'account'" class="page-stack">
      <SettingsView customer-surface />
    </section>
  </section>
</template>

<style scoped>
.customer-page { --wns-display: var(--wns-font-family); max-width: 54rem; color: var(--wns-text, #f5f5f0); }
.customer-page > header { margin-bottom: 1.25rem; }
.customer-page > header h1 { max-width: 20ch; margin: 0; font: 800 clamp(1.75rem, 1.2rem + 3vw, 3rem)/1.12 var(--wns-display); letter-spacing: -.035em; text-wrap: balance; overflow-wrap: normal; }
.customer-page > header p:not(.eyebrow) { max-width: 44rem; color: var(--wns-muted, #b2b9b6); }
.customer-page .eyebrow { color: var(--wns-mint, #b8f3d4); font: 700 .75rem/1.3 var(--wns-display); text-transform: uppercase; letter-spacing: .12em; }
.customer-page :deep(.aw-card) { color: var(--wns-text, #f5f5f0); background: var(--wns-surface, #191b1b); border: 1px solid var(--wns-line, #343939); border-radius: 24px; box-shadow: none; }
.customer-page :deep(.aw-input) { color: var(--wns-text, #f5f5f0); background: var(--wns-surface-raised, #222626); border-color: var(--wns-line, #343939); border-radius: 14px; }
.customer-page :deep(a) { color: var(--wns-text, #f5f5f0); }
.customer-page :deep(.aw-muted), .customer-page :deep(small) { color: var(--wns-muted, #b2b9b6); }
.customer-page :deep(.aw-button--primary) { min-height: 54px; color: #111513; background: var(--wns-mint, #b8f3d4); border-radius: 999px; font-weight: 750; }
.customer-page :deep(:focus-visible) { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
.customer-form { max-width: 42rem; display: grid; gap: 1rem; }
.character-extra > summary { min-height: 44px; padding-block: .65rem; cursor: pointer; }
.character-extra-fields { display: grid; gap: 1rem; padding-top: .5rem; }
.customer-actions { display: flex; flex-wrap: wrap; align-items: end; gap: .65rem; }
.character-enrichment { white-space: pre-line; padding: 1rem; border-left: 2px solid var(--wns-violet, #c8b9fc); background: var(--wns-surface-raised, #222626); }
@media (max-width: 720px) { .customer-actions { display: grid; grid-template-columns: minmax(0, 1fr); } }
</style>
