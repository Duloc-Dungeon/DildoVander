<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest } from '../identity/session';
import AdmissionManagementPanel from './AdmissionManagementPanel.vue';
import AdminReauthentication from '../identity/AdminReauthentication.vue';

interface LaunchConfig {
  revision: number;
  timeZone: string;
  personalizedLoadingEnabled: boolean;
  personalizedLoadingPrompt: string;
}
interface ArtworkJob { profileId: string; revision: number; status: string; attempt: number; errorCode?: string | null; updatedAt: number }
interface AuditEntry { actorId: string; changedAt: number; revision: number; changeSummary: string }
const config = ref<LaunchConfig | null>(null);
const jobs = ref<ArtworkJob[]>([]);
const audit = ref<AuditEntry[]>([]);
const busy = ref(false);
const error = ref('');
const auxiliaryWarning = ref('');
const reauthenticationRequired = ref(false);
const message = ref('');
const previewResult = ref('');
const previewBusy = ref(false);
const lastActionSection = ref<'messages' | 'artwork' | null>(null);
const messagesDisclosure = ref<HTMLDetailsElement>();
const artworkDisclosure = ref<HTMLDetailsElement>();
const timezone = computed(() => config.value?.timeZone || 'Europe/Prague');
const defaultPrompt = 'Napiš jednu krátkou českou větu jako napínavou předzvěst pro právě uloženou fikční postavu. Využij její jméno a nejvýše jeden neškodný veřejný fikční rys z bezpečného shrnutí, aniž prozradíš, co se chystá. Přidej suchý britský humor a drobnou narážku na Olivanderovu kocovinu.';

watch([error, reauthenticationRequired], ([currentError, needsReauthentication]) => {
  if (!currentError && !needsReauthentication) return;
  if (lastActionSection.value === 'messages' && messagesDisclosure.value) messagesDisclosure.value.open = true;
  if (lastActionSection.value === 'artwork' && artworkDisclosure.value) artworkDisclosure.value.open = true;
}, { flush: 'post' });

async function load(): Promise<void> {
  const [current, artwork, history] = await Promise.allSettled([
    accountRequest<LaunchConfig>('/app/v1/admin/launch'),
    accountRequest<{ jobs: ArtworkJob[] }>('/app/v1/admin/launch/artwork'),
    accountRequest<{ entries: AuditEntry[] }>('/app/v1/admin/launch/audit'),
  ]);
  if (current.status === 'rejected') throw current.reason;
  config.value = current.value;
  jobs.value = artwork.status === 'fulfilled' ? artwork.value.jobs : [];
  audit.value = history.status === 'fulfilled' ? history.value.entries : [];
  auxiliaryWarning.value = artwork.status === 'rejected' || history.status === 'rejected'
    ? 'Přehled portrétů nebo historie změn není dostupný. Nastavení přístupu můžete dál upravovat.' : '';
}
function showError(cause: unknown, fallback: string): void {
  reauthenticationRequired.value = (cause as { code?: string } | null)?.code === 'recent_passkey_required';
  error.value = cause instanceof Error ? cause.message : fallback;
}
async function save(): Promise<void> {
  if (!config.value || busy.value) return;
  lastActionSection.value = 'messages';
  busy.value = true; error.value = ''; message.value = '';
  try {
    const saved = await accountRequest<LaunchConfig>('/app/v1/admin/launch', 'PUT', {
      expectedRevision: config.value.revision,
      timeZone: timezone.value,
      personalizedLoadingEnabled: config.value.personalizedLoadingEnabled,
      personalizedLoadingPrompt: config.value.personalizedLoadingPrompt,
    });
    config.value = saved;
    message.value = 'Nastavení osobních vět je uložené.';
  } catch (cause) { showError(cause, 'Plán se nepodařilo uložit.'); }
  finally { busy.value = false; }
}
async function resetPrompt(): Promise<void> { if (config.value) config.value.personalizedLoadingPrompt = defaultPrompt; }
async function previewPrompt(): Promise<void> {
  if (!config.value || previewBusy.value) return;
  lastActionSection.value = 'messages';
  previewBusy.value = true; previewResult.value = ''; error.value = '';
  try {
      const result = await accountRequest<{ message: string }>('/app/v1/admin/launch/personalization-preview', 'POST', { name: 'Alžběta ze světa za zrcadlem', universe: 'Pohádka o říši za zrcadlem', prompt: config.value.personalizedLoadingPrompt });
    previewResult.value = result.message;
  } catch (cause) { showError(cause, 'Ukázkovou větu se nepodařilo připravit.'); }
  finally { previewBusy.value = false; }
}
async function retry(job: ArtworkJob): Promise<void> {
  if (busy.value) return;
  lastActionSection.value = 'artwork';
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/artwork/${encodeURIComponent(job.profileId)}/retry`, 'POST', { expectedRevision: job.revision });
    message.value = 'Nový pokus o přípravu portrétu byl zařazen.';
    await load();
  } catch (cause) { showError(cause, 'Nový pokus se nepodařilo zařadit.'); }
  finally { busy.value = false; }
}
onMounted(() => {
  void load().catch(cause => showError(cause, 'Nastavení se nepodařilo načíst.'));
});
</script>

<template>
  <section class="launch-admin page-stack" aria-labelledby="launch-admin-title">
    <header><h2 id="launch-admin-title">Příprava večera</h2><p>Vyberte skupinu, zkontrolujte hosty a nastavte otevření. Pokročilé volby najdete níže.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AdminReauthentication v-if="reauthenticationRequired" @verified="reauthenticationRequired = false; error = ''; message = 'Totožnost byla ověřena. Původní změnu nyní odešlete znovu.'" />
    <AwNotice v-if="auxiliaryWarning" tone="warning">{{ auxiliaryWarning }}</AwNotice>
    <AwNotice v-if="message" tone="info" role="status">{{ message }}</AwNotice>
    <AdmissionManagementPanel />
    <p v-if="!config && !error" role="status">Načítání nastavení…</p>
    <template v-if="config">
      <details ref="messagesDisclosure" class="launch-admin__disclosure">
        <summary>Osobní věty při přípravě postavy</summary>
        <section class="aw-card launch-admin__personalization">
          <h3>Osobní věta o postavě</h3>
          <label><input v-model="config.personalizedLoadingEnabled" type="checkbox" /> Generovat osobní větu během přípravy</label>
          <AwField id="launch-prompt" label="Pokyn pro osobní větu"><textarea id="launch-prompt" v-model="config.personalizedLoadingPrompt" class="aw-input" rows="5" maxlength="4000" /></AwField>
          <p>Pokyn platí pro nově připravované postavy. Ukázka spotřebuje AI prostředky.</p>
          <div class="launch-admin__actions"><AwButton variant="secondary" @click="resetPrompt">Obnovit výchozí pokyn</AwButton><AwButton variant="secondary" :busy="previewBusy" @click="previewPrompt">Vytvořit ukázku</AwButton></div>
          <p v-if="previewResult" class="launch-admin__preview-copy" role="status">{{ previewResult }}</p>
          <AwButton :busy="busy" @click="save">Uložit osobní věty</AwButton>
        </section>
      </details>
      <details ref="artworkDisclosure" class="launch-admin__disclosure">
        <summary>Příprava portrétů ({{ jobs.length }})</summary>
        <section class="aw-card"><h3>Příprava portrétů</h3><p v-if="!jobs.length">Žádné rozpracované portréty.</p><ul class="launch-jobs"><li v-for="job in jobs" :key="`${job.profileId}:${job.revision}`"><span><strong>{{ job.status }}</strong> · pokus {{ job.attempt }}<small v-if="job.errorCode"> · {{ job.errorCode }}</small></span><AwButton v-if="job.status === 'failed'" variant="secondary" :disabled="busy" @click="retry(job)">Opakovat přípravu</AwButton></li></ul></section>
      </details>
      <details class="launch-admin__disclosure">
        <summary>Historie změn ({{ audit.length }})</summary>
        <section class="aw-card"><h3>Historie změn</h3><p v-if="!audit.length">Zatím bez změn.</p><ul v-else class="launch-audit"><li v-for="entry in audit" :key="entry.revision"><time :datetime="new Date(entry.changedAt).toISOString()">{{ new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short', timeZone: timezone }).format(entry.changedAt) }}</time><span>{{ entry.changeSummary }}</span><small>Správce {{ entry.actorId }} · revize {{ entry.revision }}</small></li></ul></section>
      </details>

    </template>
  </section>
</template>

<style scoped>
.launch-admin { min-width: 0; border-top: 1px solid var(--aw-border-subtle); padding-top: 2rem; margin-top: 2rem; }
.launch-admin h3 { margin-top: 0; }
.launch-admin__preview-copy { padding: 1rem; border: 1px solid var(--aw-border-subtle); background: var(--aw-void); color: var(--aw-muted); line-height: 1.6; overflow-wrap: anywhere; }
.launch-admin__actions { display: flex; gap: .75rem; flex-wrap: wrap; }
.launch-jobs, .launch-audit { list-style: none; padding: 0; margin-block: 1rem 0; }
.launch-jobs li, .launch-audit li { display: flex; justify-content: space-between; gap: 1rem; align-items: center; padding: .8rem 0; border-top: 1px solid var(--aw-border-subtle); }
.launch-jobs li > span { min-width: 0; overflow-wrap: anywhere; }
.launch-audit li { align-items: flex-start; flex-direction: column; overflow-wrap: anywhere; }
.launch-jobs small, .launch-audit small { color: var(--aw-muted); }
@media (max-width: 520px) {
  .launch-jobs li { align-items: flex-start; flex-direction: column; }
}
.launch-admin__disclosure { min-width: 0; border-top: 1px solid var(--aw-border-subtle); padding-block: .5rem; }
.launch-admin__disclosure > summary { min-height: 48px; display: list-item; list-style-position: inside; padding-block: .75rem; cursor: pointer; font-weight: 700; overflow-wrap: anywhere; }
.launch-admin__disclosure[open] > .aw-card { margin-top: .75rem; padding: 0; border: 0; border-radius: 0; background: transparent; }
.launch-admin__personalization { display: grid; gap: 1rem; }
</style>
