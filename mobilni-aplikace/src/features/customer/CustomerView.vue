<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import GeneratedWand from '../archive/GeneratedWand.vue';
import { AccountArchiveClient } from '../archive/accountArchive';
import { projectWandArtwork, useWandArtworkRetry, type WandArtwork } from '../archive/wandArtwork';
import { useArchiveMedia } from '../archive/useArchiveMedia';
import { isUuid } from '../archive/client';
import { projectMobileGeneratedWand, type GeneratedWand as GeneratedWandSnapshot } from '../archive/generatedWand';
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
import type { PrelaunchCharacterResponse } from '../launch/generatedPrelaunchCharacter';

interface CharacterProfile {
  profileId: string; revision: number; label?: string | null;
  userInput: { name: string; world: string; version?: string; description?: string; language?: string };
  status: string; enrichment?: unknown; errorCode?: string; updatedAt?: string;
}
const route = useRoute();
watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true, flush: 'sync' });
const pane = computed(() => route.path.split('/').at(-1) ?? 'character');
const profile = ref<CharacterProfile | null>(null);
const accountCharacter = ref<PrelaunchCharacterResponse['character']>(null);
const selectedWand = ref<Omit<GeneratedWandSnapshot, 'physicalSlot'> | null>(null);
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
const characterNextStep = computed(() => {
  if (!profile.value) return null;
  if (selectedWand.value) return { to: '/play', label: 'Přejít ke hrám' };
  if (['saved', 'ready'].includes(profile.value.status)) return { to: '/play/wand', label: 'Otevřít dílnu' };
  if (['queued', 'running'].includes(profile.value.status)) return null;
  return { to: '/support', label: 'Pomoc s přípravou' };
});
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
  accountCharacter.value = null;
  selectedWand.value = null;
  profileLoading.value = Boolean(account.value);
  busy.value = false;
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
    selectedWand.value = result.profile ? projectMobileGeneratedWand(result.generatedWand) : null;
    if (pane.value === 'character') bindProfileArtwork(result.artwork);
    form.value = {
      name: result.profile?.userInput.name ?? '', world: result.profile?.userInput.world ?? '',
      version: result.profile?.userInput.version ?? '', description: result.profile?.userInput.description ?? '',
      language: result.profile?.userInput.language ?? '',
    };
    scheduleEnrichmentRefresh(epoch);
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) profileLoading.value = false; }
}

async function loadAccountCharacter(epoch = generation): Promise<void> {
  try {
    // Account settings remain accessible before launch; use the same bounded
    // character identity endpoint as the waiting room and personal caption.
    const result = await accountRequest<PrelaunchCharacterResponse>('/app/v1/prelaunch/character', 'GET', undefined, accountWorkController.signal);
    if (epoch === generation) accountCharacter.value = result.character;
  } catch {
    // Optional personal copy must never block account settings.
  } finally { if (epoch === generation) profileLoading.value = false; }
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
  if (pane.value !== 'character') return;
  if (profile.value && (['queued', 'running'].includes(profile.value.status) || (['queued', 'running'].includes(artworkState.value?.status ?? '') || artworkState.value?.status === 'missing' && artworkState.value.canRetry))) {
    enrichmentTimer = setTimeout(() => { void refreshEnrichment(epoch); }, 3000);
  }
}
async function refreshEnrichment(epoch = generation): Promise<void> {
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null; generatedWand?: unknown; artwork?: unknown }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation || !result.profile || result.profile.profileId !== profile.value?.profileId || result.profile.revision !== profile.value.revision) return;
    selectedWand.value = projectMobileGeneratedWand(result.generatedWand);
    bindProfileArtwork(result.artwork);
    // Refresh server-derived enrichment only; never replace the visitor's in-progress form edits.
    profile.value = { ...profile.value, status: result.profile.status, enrichment: result.profile.enrichment, errorCode: result.profile.errorCode, updatedAt: result.profile.updatedAt };
    scheduleEnrichmentRefresh(epoch);
  } catch { if (epoch === generation) scheduleEnrichmentRefresh(epoch); }
}

function showProjectedError(cause: unknown): void {
  errorFeedback.value = projectCustomerSessionError(cause);
}

watch([() => account.value?.id, pane], () => {
  resetPrivateState();
  if (account.value && pane.value === 'character') void loadProfile(generation);
  if (account.value && pane.value === 'account') void loadAccountCharacter(generation);
}, { immediate: true, flush: 'sync' });
onBeforeUnmount(resetPrivateState);
</script>

<template>
  <section class="page-stack customer-page" :class="`customer-page--${pane}`" :aria-labelledby="`customer-${pane}-title`">
    <header class="customer-page__header"><div><h1 :id="`customer-${pane}-title`">{{ customerCopy.text(paneTitleKeys[pane] ?? 'customer.space') }}</h1><p v-if="pane === 'character' && !profile">{{ customerCopy.text('customer.character-header') }}</p></div><RouterLink v-if="pane === 'character'" to="/me/account">{{ customerCopy.text('customer.account-pane') }}</RouterLink><RouterLink v-else-if="pane === 'account'" to="/me/character">{{ customerCopy.text('customer.character-pane') }}</RouterLink></header>
    <AwNotice v-if="errorFeedback" tone="warning" role="alert">{{ errorFeedback.copyKey ? customerCopy.text(errorFeedback.copyKey) : errorFeedback.message }}<small v-if="errorFeedback.diagnosticCode"> {{ customerCopy.text('customer.diagnostic', { code: errorFeedback.diagnosticCode }) }}</small></AwNotice>
    <AwNotice v-if="noticeCopyKey" tone="info" role="status">{{ customerCopy.text(noticeCopyKey) }}</AwNotice>

    <p v-if="pane === 'character' && profileLoading" role="status">{{ customerCopy.text('customer.profile-loading') }}</p>
    <section v-if="pane === 'character' && !profileLoading" class="page-stack">
      <section v-if="profile" class="character-overview" aria-labelledby="character-confirmation-title">
        <div class="character-overview__identity">
          <h2 id="character-confirmation-title">{{ characterDisplayName(profile.userInput.name, profile.label) }}</h2>
          <p class="aw-muted">{{ profile.userInput.world }}<span v-if="profile.userInput.version"> · {{ profile.userInput.version }}</span></p>
          <p class="character-overview__status" role="status">{{ customerCopy.text(profileStatusKeys[profile.status] ?? 'customer.status-unknown') }}</p>
        </div>
        <p v-if="profile.status === 'saved'">{{ customerCopy.text('customer.saved-guidance') }}</p>
        <p v-if="['queued', 'running'].includes(profile.status)" class="aw-muted">Postava se připravuje. Stav se tu průběžně obnovuje.</p>
        <AwNotice v-if="clarificationText" tone="info">{{ customerCopy.text('customer.clarification', { text: clarificationText }) }}</AwNotice>
        <RouterLink v-if="characterNextStep" class="aw-button aw-button--primary character-next-step" :to="characterNextStep.to">{{ characterNextStep.label }}</RouterLink>
      </section>
      <PrivateCharacterAvatar v-if="profile" :profile-id="profile.profileId" :revision="profile.revision" :name="profile.userInput.name" />
      <details v-if="profile && selectedWand" class="character-details">
        <summary>Tvoje hůlka · {{ selectedWand.name }}</summary>
        <GeneratedWand :wand="selectedWand" mobile show-name
        :image-url="profileArtwork.artworkUrl.value"
        :image-pending="!!profileArtworkPath && !profileArtwork.artworkUrl.value && !profileArtwork.artworkError.value"
        :image-error="profileArtwork.artworkError.value" :image-state="artworkState?.status" :synthetic="artworkState?.synthetic"
        :can-retry="artworkState?.canRetry" :retry-busy="artworkRetry.busy.value" @retry="artworkRetry.retry(artworkState, () => refreshEnrichment())"
        :keepsake-to="artworkState ? `/keepsake/?account=1&k=${artworkState.locator}` : undefined" history-to="/me/keepsakes" />
        <p v-if="artworkRetry.failed.value" role="alert">Opakování se nepodařilo potvrdit. Před dalším pokusem obnov stav.</p>
      </details>
      <details v-if="profile" class="character-details">
        <summary>Označení a podrobnosti postavy</summary>
        <CharacterLabelEditor :profile-id="profile.profileId" :label="profile.label" @saved="profile.label = $event" />
        <p class="aw-muted">{{ customerCopy.text('customer.revision', { revision: profile.revision }) }}</p>
        <p v-if="!canEditCharacter" class="aw-muted">Na účtu máš jednu postavu. Její úpravy řeší administrátor.</p>
      </details>
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
    </section>

    <CustomerKeepsakes v-else-if="pane === 'keepsakes'" />
    <section v-else-if="pane === 'account'" class="page-stack">
      <PrivateCharacterAvatar v-if="accountCharacter" text-only :profile-id="accountCharacter.profileId" :revision="accountCharacter.revision" :name="accountCharacter.name" />
      <SettingsView customer-surface />
    </section>
  </section>
</template>

<style scoped>
.customer-page { --wns-display: var(--wns-font-family); container: customer-page / inline-size; width: 100%; margin-inline: auto; gap: var(--wns-space-32); color: var(--wns-text, #f5f5f0); }
.customer-page--character { max-width: 62rem; }
.customer-page__header { display: flex; align-items: baseline; justify-content: space-between; flex-wrap: wrap; gap: var(--wns-space-16); }
.customer-page__header > a { min-height: 44px; display: inline-flex; align-items: center; }
.customer-page--account { max-width: 48rem; }
.customer-page > header { display: grid; gap: var(--wns-space-12); padding: 0; }
.customer-page > header h1 { max-width: 20ch; margin: 0; font: 800 clamp(2rem, 1.25rem + 3vw, 3.5rem)/1.08 var(--wns-display); letter-spacing: -.035em; text-wrap: balance; overflow-wrap: anywhere; }
.customer-page > header p { max-width: 65ch; margin: 0; color: var(--wns-muted, #b2b9b6); }
.customer-page .eyebrow { color: var(--wns-mint, #b8f3d4); font: 700 .75rem/1.3 var(--wns-display); text-transform: uppercase; letter-spacing: .12em; }
.customer-page :deep(.aw-card) { color: var(--wns-text, #f5f5f0); background: var(--wns-surface, #191b1b); border: 1px solid var(--wns-line, #343939); border-radius: var(--wns-radius-card); box-shadow: none; }
.customer-page :deep(.aw-input) { color: var(--wns-text, #f5f5f0); background: var(--wns-surface-raised, #222626); border-color: var(--wns-line, #343939); border-radius: 14px; }
.customer-page :deep(a:not(.aw-button)) { color: var(--wns-mint); }
.customer-page :deep(.aw-muted), .customer-page :deep(small) { color: var(--wns-muted, #b2b9b6); }
.customer-page :deep(:focus-visible) { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
.customer-form { width: 100%; min-width: 0; max-width: 42rem; display: grid; gap: var(--wns-space-20); }
.customer-form :deep(.aw-input) { width: 100%; min-width: 0; }
.character-overview { display: grid; gap: var(--wns-space-20); padding-block-end: var(--wns-space-24); border-bottom: 1px solid var(--wns-line); }
.character-overview__identity { display: grid; gap: var(--wns-space-8); }
.character-overview h2 { margin: 0; font-size: clamp(1.6rem, 5vw, 2.5rem); line-height: 1.12; letter-spacing: -.025em; }
.character-overview p { margin: 0; max-width: 65ch; }
.character-overview__status { color: var(--wns-mint); }
.customer-page .character-next-step { width: 100%; max-width: 24rem; justify-self: start; border-color: transparent; box-shadow: none; }
.character-details { border-top: 1px solid var(--wns-line); }
.character-details > summary { min-height: 48px; padding-block: var(--wns-space-12); cursor: pointer; font-weight: 700; line-height: 1.5; }
.character-details[open] > summary { margin-bottom: var(--wns-space-16); }
.character-details > :deep(.page-stack) { margin-block: var(--wns-space-16); }
.character-extra > summary { min-height: 44px; padding-block: .65rem; cursor: pointer; }
.character-extra-fields { display: grid; gap: 1rem; padding-top: .5rem; }
.customer-actions { display: flex; flex-wrap: wrap; align-items: end; gap: .65rem; }
.character-enrichment { white-space: pre-line; padding: var(--wns-space-20); border-radius: var(--wns-radius-field); background: var(--wns-surface-raised); }
@media (max-width: 720px) { .customer-actions { display: grid; grid-template-columns: minmax(0, 1fr); } }
</style>
