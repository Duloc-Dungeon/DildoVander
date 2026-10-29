<script setup lang="ts">
import curtain from '../../assets/brand/invitation-curtain-small.webp';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest } from '../identity/session';
import BetaAdmissionPanel from './BetaAdmissionPanel.vue';
import AdminReauthentication from '../identity/AdminReauthentication.vue';

interface LaunchConfig {
  revision: number;
  betaEnabled: boolean;
  betaLaunchAt: number | null;
  betaUnlocked: boolean;
  productionEnabled: boolean;
  productionLaunchAt: number | null;
  productionUnlocked: boolean;
  timeZone: string;
  teaser: string;
  initialLoadingMessage: string;
  personalizedLoadingEnabled: boolean;
  personalizedLoadingPrompt: string;
}
interface ArtworkJob { profileId: string; revision: number; status: string; attempt: number; errorCode?: string | null; updatedAt: number }
interface AuditEntry { actorId: string; changedAt: number; revision: number; changeSummary: string }
const config = ref<LaunchConfig | null>(null);
const betaDate = ref('');
const productionDate = ref('');
const jobs = ref<ArtworkJob[]>([]);
const audit = ref<AuditEntry[]>([]);
const busy = ref(false);
const error = ref('');
const auxiliaryWarning = ref('');
const reauthenticationRequired = ref(false);
const message = ref('');
const previewStage = ref('pending');
const previewAudience = ref<'beta' | 'production'>('production');
const previewNow = ref(Date.now());
const previewResult = ref('');
const previewBusy = ref(false);
const lastActionSection = ref<'schedule' | 'messages' | 'artwork' | null>(null);
const scheduleDisclosure = ref<HTMLDetailsElement>();
const messagesDisclosure = ref<HTMLDetailsElement>();
const artworkDisclosure = ref<HTMLDetailsElement>();
async function openProductionSchedule(): Promise<void> {
  if (!scheduleDisclosure.value) return;
  scheduleDisclosure.value.open = true;
  await nextTick();
  scheduleDisclosure.value.querySelector<HTMLInputElement>('#production-launch')?.focus();
}
const timezone = computed(() => config.value?.timeZone || 'Europe/Prague');
const upcomingPreview = computed(() => previewAudience.value === 'beta' ? betaDate.value : productionDate.value);
const displayedCountdown = computed(() => {
  if (!upcomingPreview.value) return 'Datum není nastaveno';
  const at = parsePrague(upcomingPreview.value);
  if (at == null) return 'Datum nebo čas je neplatný';
  const minutes = Math.max(0, Math.floor((at - previewNow.value) / 60_000));
  return `${Math.floor(minutes / 1440)} dní · ${Math.floor(minutes / 60) % 24} hodin · ${minutes % 60} minut`;
});
const previewStages: Record<string, string> = { pending: 'Za zavřenými dveřmi', generated: 'Odhalení', failed: 'Něco se zadrhlo', enrichment: 'Tvé jméno už zaznělo' };
const defaultPrompt = 'Napiš jednu krátkou českou větu jako napínavou předzvěst pro právě uloženou fikční postavu. Využij její jméno a nejvýše jeden neškodný veřejný fikční rys z bezpečného shrnutí, aniž prozradíš, co se chystá. Přidej suchý britský humor a drobnou narážku na Olivanderovu kocovinu.';

watch([error, reauthenticationRequired], ([currentError, needsReauthentication]) => {
  if (!currentError && !needsReauthentication) return;
  if (lastActionSection.value === 'schedule' && scheduleDisclosure.value) scheduleDisclosure.value.open = true;
  if (lastActionSection.value === 'messages' && messagesDisclosure.value) messagesDisclosure.value.open = true;
  if (lastActionSection.value === 'artwork' && artworkDisclosure.value) artworkDisclosure.value.open = true;
}, { flush: 'post' });

function pragueParts(timestamp: number): [number, number, number, number, number, number] {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: timezone.value, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(timestamp);
  const get = (type: string) => Number(parts.find(part => part.type === type)?.value ?? 0);
  return [get('year'), get('month'), get('day'), get('hour'), get('minute'), get('second')];
}
function toPragueDate(timestamp: number | null): string {
  if (timestamp == null) return '';
  const p = pragueParts(timestamp);
  return `${p[0]}-${String(p[1]).padStart(2, '0')}-${String(p[2]).padStart(2, '0')}T${String(p[3]).padStart(2, '0')}:${String(p[4]).padStart(2, '0')}`;
}
function parsePrague(value: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const desired = match.slice(1).map(Number);
  const naive = Date.UTC(desired[0], desired[1] - 1, desired[2], desired[3], desired[4]);
  let candidate = naive;
  for (let attempt = 0; attempt < 3; attempt++) {
    const p = pragueParts(candidate);
    const represented = Date.UTC(p[0], p[1] - 1, p[2], p[3], p[4], p[5]);
    candidate += naive - represented;
  }
  const resolved = pragueParts(candidate);
  return resolved.slice(0, 5).every((value, index) => value === desired[index]) ? candidate : null;
}
function hydrate(value: LaunchConfig): void {
  config.value = value;
  betaDate.value = toPragueDate(value.betaLaunchAt);
  productionDate.value = toPragueDate(value.productionLaunchAt);
}
watch(() => config.value?.timeZone, () => {
  if (!config.value) return;
  betaDate.value = toPragueDate(config.value.betaLaunchAt);
  productionDate.value = toPragueDate(config.value.productionLaunchAt);
});
async function load(): Promise<void> {
  const [current, artwork, history] = await Promise.allSettled([
    accountRequest<LaunchConfig>('/app/v1/admin/launch'),
    accountRequest<{ jobs: ArtworkJob[] }>('/app/v1/admin/launch/artwork'),
    accountRequest<{ entries: AuditEntry[] }>('/app/v1/admin/launch/audit'),
  ]);
  if (current.status === 'rejected') throw current.reason;
  hydrate(current.value);
  jobs.value = artwork.status === 'fulfilled' ? artwork.value.jobs : [];
  audit.value = history.status === 'fulfilled' ? history.value.entries : [];
  auxiliaryWarning.value = artwork.status === 'rejected' || history.status === 'rejected'
    ? 'Přehled portrétů nebo historie změn není dostupný. Nastavení přístupu můžete dál upravovat.' : '';
}
function showError(cause: unknown, fallback: string): void {
  reauthenticationRequired.value = (cause as { code?: string } | null)?.code === 'recent_passkey_required';
  error.value = cause instanceof Error ? cause.message : fallback;
}
async function save(section: 'schedule' | 'messages'): Promise<void> {
  if (!config.value || busy.value) return;
  lastActionSection.value = section;
  const betaLaunchAt = betaDate.value ? parsePrague(betaDate.value) : null;
  const productionLaunchAt = productionDate.value ? parsePrague(productionDate.value) : null;
  if ((betaDate.value && betaLaunchAt == null) || (productionDate.value && productionLaunchAt == null)) {
    lastActionSection.value = 'schedule';
    error.value = `Zadaný místní čas v pásmu ${timezone.value} neexistuje kvůli změně letního času. Vyberte jiný čas.`;
    return;
  }
  if (config.value.teaser.length > 250) { lastActionSection.value = 'messages'; error.value = 'Krátké sdělení smí mít nejvýše 250 znaků.'; return; }
  busy.value = true; error.value = ''; message.value = '';
  try {
    const saved = await accountRequest<LaunchConfig>('/app/v1/admin/launch', 'PUT', {
      expectedRevision: config.value.revision,
      betaEnabled: config.value.betaEnabled,
      betaLaunchAt,
      betaUnlocked: config.value.betaUnlocked,
      productionEnabled: config.value.productionEnabled,
      productionLaunchAt,
      productionUnlocked: config.value.productionUnlocked,
      timeZone: timezone.value,
      teaser: config.value.teaser,
      initialLoadingMessage: config.value.initialLoadingMessage,
      personalizedLoadingEnabled: config.value.personalizedLoadingEnabled,
      personalizedLoadingPrompt: config.value.personalizedLoadingPrompt,
    });
    hydrate(saved);
    message.value = 'Plán a krátké sdělení jsou uložené.';
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
let previewTimer: ReturnType<typeof setInterval> | undefined;
onMounted(() => {
  void load().catch(cause => showError(cause, 'Nastavení se nepodařilo načíst.'));
  previewTimer = setInterval(() => { previewNow.value = Date.now(); }, 1000);
});
onBeforeUnmount(() => { if (previewTimer) clearInterval(previewTimer); });
</script>

<template>
  <section class="launch-admin page-stack" aria-labelledby="launch-admin-title">
    <header><h2 id="launch-admin-title">Příprava večera</h2><p>Vyberte skupinu, zkontrolujte hosty a nastavte otevření. Pokročilé volby najdete níže.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AdminReauthentication v-if="reauthenticationRequired" @verified="reauthenticationRequired = false; error = ''; message = 'Totožnost byla ověřena. Původní změnu nyní odešlete znovu.'" />
    <AwNotice v-if="auxiliaryWarning" tone="warning">{{ auxiliaryWarning }}</AwNotice>
    <AwNotice v-if="message" tone="info" role="status">{{ message }}</AwNotice>
    <BetaAdmissionPanel @edit-production-schedule="openProductionSchedule" />
    <p v-if="!config && !error" role="status">Načítání nastavení…</p>
    <template v-if="config">
      <details ref="scheduleDisclosure" class="launch-admin__disclosure">
        <summary>Datum a čas otevření výchozí skupiny</summary>
        <section class="aw-card launch-admin__schedule"><h3>Čas otevření</h3><p>Brány pro beta a produkční publikum platí společně pro web i iOS. Vydání jednotlivých distribucí se provádí samostatně. Časy se zadávají a zobrazují v pásmu <strong>{{ config.timeZone }}</strong>, včetně změn letního času.</p>
          <label for="launch-time-zone">Časové pásmo</label><select id="launch-time-zone" v-model="config.timeZone" class="aw-select"><option value="Europe/Prague">Europe/Prague · Praha</option><option value="UTC">UTC</option><option value="America/New_York">America/New_York · New York</option><option value="America/Los_Angeles">America/Los_Angeles · Los Angeles</option></select>
          <fieldset><legend>Beta pozvaní</legend><label><input v-model="config.betaEnabled" type="checkbox" /> Použít beta odpočítávání</label><AwField id="beta-launch" label="Otevření beta účastníkům"><input id="beta-launch" v-model="betaDate" class="aw-input" type="datetime-local" /></AwField><label><input v-model="config.betaUnlocked" type="checkbox" /> Ručně otevřít beta přístup</label><p class="launch-admin__effect">{{ config.betaUnlocked ? 'Beta účty získají plný přístup ihned po uložení.' : config.betaEnabled ? (betaDate ? 'Aktivní bude přístup od zvoleného času.' : 'Beta plán čeká na datum.') : 'Beta plán je vypnutý; beta účty zachovávají dosavadní chráněný přístup.' }}</p></fieldset>
          <fieldset><legend>Produkce a výchozí skupina</legend><label><input v-model="config.productionEnabled" type="checkbox" /> Použít produkční odpočítávání</label><AwField id="production-launch" label="Otevření večera výchozí skupině"><input id="production-launch" v-model="productionDate" class="aw-input" type="datetime-local" /></AwField><label><input v-model="config.productionUnlocked" type="checkbox" /> Ručně otevřít produkční přístup</label><p class="launch-admin__effect">{{ config.productionUnlocked ? 'Výchozí skupina i produkční účty bez skupiny získají plný přístup ihned po uložení.' : config.productionEnabled ? (productionDate ? 'Hosté výchozí skupiny počkají do zvoleného data a času. Večer se potom otevře automaticky.' : 'Hosté výchozí skupiny čekají na nastavení data a času.') : 'Odpočítávání je vypnuté. Výchozí skupina i produkční účty bez skupiny mají plný přístup.' }}</p><p>Změna budoucího termínu vrátí výchozí skupinu do čekárny i po dřívějším otevření. Přijímání nových účastníků se nastavuje samostatně.</p></fieldset>
          <div class="launch-admin__preview"><p class="eyebrow">Živý náhled · {{ previewAudience === 'beta' ? 'beta' : 'produkce' }}</p><label for="launch-audience">Publikum</label><select id="launch-audience" v-model="previewAudience" class="aw-select"><option value="beta">Beta pozvaní</option><option value="production">Produkce</option></select><strong>{{ displayedCountdown }}</strong></div>
          <AwNotice tone="warning">Zapnutí plánu s časem v minulosti nebo ručním odemčením změní přístup hned po uložení.</AwNotice>
          <AwButton :busy="busy" @click="save('schedule')">Uložit plán</AwButton>
        </section>
      </details>
      <details ref="messagesDisclosure" class="launch-admin__disclosure">
        <summary>Zprávy a osobní věta</summary>
        <section class="aw-card"><h3>Zpráva pro pozvané</h3><label for="launch-teaser">Krátké sdělení · {{ config.teaser.length }}/250</label><textarea id="launch-teaser" v-model="config.teaser" class="aw-input" maxlength="250" rows="3" /><p class="launch-admin__preview-copy">{{ config.teaser || 'Náhled krátkého sdělení.' }}</p><AwField id="launch-loading" label="Zpráva během přípravy portrétu"><input id="launch-loading" v-model="config.initialLoadingMessage" class="aw-input" maxlength="180" /></AwField><label><input v-model="config.personalizedLoadingEnabled" type="checkbox" /> Zapnout osobní větu o postavě</label><AwField id="launch-prompt" label="Pokyn pro osobní větu"><textarea id="launch-prompt" v-model="config.personalizedLoadingPrompt" class="aw-input" rows="5" maxlength="4000" /></AwField><p>Host má cítit napětí, ne číst popis výroby. Nezmiňujte portrét, malování, rešerše ani kroky zpracování. Pokyn doplňují pevná pravidla soukromí a tajemství; ukázka spotřebuje AI prostředky.</p><div class="launch-admin__actions"><AwButton variant="secondary" @click="resetPrompt">Obnovit výchozí pokyn</AwButton><AwButton variant="secondary" :busy="previewBusy" @click="previewPrompt">Vytvořit ukázku</AwButton></div><p v-if="previewResult" class="launch-admin__preview-copy" role="status">{{ previewResult }}</p><p>Uloží se i rozepsané změny globálního plánu.</p><AwButton :busy="busy" @click="save('messages')">Uložit plán i zprávy</AwButton></section>
      </details>
      <details ref="artworkDisclosure" class="launch-admin__disclosure">
        <summary>Příprava portrétů ({{ jobs.length }})</summary>
        <section class="aw-card"><h3>Příprava portrétů</h3><p v-if="!jobs.length">Žádné rozpracované portréty.</p><ul class="launch-jobs"><li v-for="job in jobs" :key="`${job.profileId}:${job.revision}`"><span><strong>{{ job.status }}</strong> · pokus {{ job.attempt }}<small v-if="job.errorCode"> · {{ job.errorCode }}</small></span><AwButton v-if="job.status === 'failed'" variant="secondary" :disabled="busy" @click="retry(job)">Opakovat přípravu</AwButton></li></ul></section>
      </details>
      <details class="launch-admin__disclosure">
        <summary>Historie změn ({{ audit.length }})</summary>
        <section class="aw-card"><h3>Historie změn</h3><p v-if="!audit.length">Zatím bez změn.</p><ul v-else class="launch-audit"><li v-for="entry in audit" :key="entry.revision"><time :datetime="new Date(entry.changedAt).toISOString()">{{ new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short', timeZone: timezone }).format(entry.changedAt) }}</time><span>{{ entry.changeSummary }}</span><small>Správce {{ entry.actorId }} · revize {{ entry.revision }}</small></li></ul></section>
      </details>
      <details class="launch-admin__disclosure">
        <summary>Náhled čekárny</summary>
        <section class="aw-card launch-admin__experience"><h3>Náhled čekárny</h3><label for="launch-stage">Stav</label><select id="launch-stage" v-model="previewStage" class="aw-select"><option v-for="(label, key) in previewStages" :key="key" :value="key">{{ label }}</option></select><div class="launch-preview-card" :class="`stage-${previewStage}`"><div class="launch-preview-art" aria-hidden="true"><img :src="curtain" alt="" width="480" height="640" /></div><div><p>Ukázka · Wand Night Stand</p><strong>{{ previewStage === 'generated' ? 'Alžběta, už tě vidíme.' : previewStage === 'failed' ? 'Něco se zpozdilo.' : 'Něco se chystá, Alžběta.' }}</strong><p>{{ config.teaser }}</p><small>{{ previewStage === 'generated' ? 'Portrét je připraven.' : previewStage === 'failed' ? 'Portrét čeká na další pokus.' : previewStage === 'enrichment' && config.personalizedLoadingEnabled ? 'Zrcadlo si vybírá správnou větu.' : config.initialLoadingMessage }}</small><span class="launch-preview-countdown">{{ displayedCountdown }}</span></div></div></section>
      </details>
    </template>
  </section>
</template>

<style scoped>
.launch-admin{border-top:1px solid var(--aw-border-subtle);padding-top:2rem;margin-top:2rem}.launch-admin h3{margin-top:0}.launch-admin__schedule{display:grid;gap:1rem}.launch-admin__schedule fieldset{border:1px solid var(--aw-border-subtle);border-radius:.65rem;padding:1rem;display:grid;gap:.85rem}.launch-admin__schedule legend{font-weight:650;padding-inline:.4rem}.launch-admin__effect{color:var(--aw-muted);font-size:.85rem}.launch-admin__preview{display:grid;grid-template-columns:1fr auto;gap:.65rem;align-items:center;padding:1rem;border-radius:.6rem;background:var(--aw-void)}.launch-admin__preview .eyebrow{grid-column:1/-1;margin:0}.launch-admin__preview strong{grid-column:1/-1;font-size:1.4rem}.launch-admin__preview-copy{padding:1rem;border:1px solid var(--aw-border-subtle);background:var(--aw-void);color:var(--aw-muted);line-height:1.6}.launch-admin__actions{display:flex;gap:.75rem;flex-wrap:wrap}.launch-jobs,.launch-audit{list-style:none;padding:0}.launch-jobs li,.launch-audit li{display:flex;justify-content:space-between;gap:1rem;align-items:center;padding:.8rem 0;border-top:1px solid var(--aw-border-subtle)}.launch-audit li{align-items:flex-start;flex-direction:column}.launch-jobs small,.launch-audit small{color:var(--aw-muted)}.launch-preview-card{display:grid;grid-template-columns:minmax(7rem,.55fr) minmax(0,1fr);align-items:center;gap:1.5rem;margin-top:1rem;padding:1.25rem;border:1px solid var(--aw-border-subtle);border-radius:1rem;background:var(--aw-surface);max-width:46rem}.launch-preview-art{width:100%;max-width:9rem;aspect-ratio:4/5;border-radius:12px;overflow:hidden;background:#171827}.launch-preview-art img{width:100%;height:100%;object-fit:cover}.launch-preview-card>div:last-child{display:grid;gap:.6rem}.launch-preview-card strong{font-size:1.2rem}.launch-preview-card p{margin:0;color:var(--aw-muted);line-height:1.5}.launch-preview-card small{color:var(--aw-accent)}.launch-preview-countdown{font-variant-numeric:tabular-nums;font-size:.83rem;color:var(--aw-ivory)}.stage-failed .launch-preview-art{filter:grayscale(.8);opacity:.55}@media(max-width:520px){.launch-jobs li{align-items:flex-start;flex-direction:column}.launch-preview-card{grid-template-columns:1fr}.launch-preview-art{max-width:7rem}}
.launch-admin__disclosure { border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); padding: .75rem 1rem; }
.launch-admin__disclosure > summary { min-height: 44px; display: list-item; list-style-position: inside; padding-block: .65rem; cursor: pointer; font-weight: 700; }
.launch-admin__disclosure[open] > .aw-card { margin-top: .75rem; }
</style>
