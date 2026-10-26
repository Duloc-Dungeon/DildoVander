<script setup lang="ts">
import curtain from '../../assets/brand/invitation-curtain-small.webp';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest } from '../identity/session';
import BetaAdmissionPanel from './BetaAdmissionPanel.vue';

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
const message = ref('');
const previewStage = ref('pending');
const previewAudience = ref<'beta' | 'production'>('production');
const previewNow = ref(Date.now());
const previewCopy = ref('');
const previewResult = ref('');
const previewBusy = ref(false);
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
  const [current, artwork, history] = await Promise.all([
    accountRequest<LaunchConfig>('/app/v1/admin/launch'),
    accountRequest<{ jobs: ArtworkJob[] }>('/app/v1/admin/launch/artwork'),
    accountRequest<{ entries: AuditEntry[] }>('/app/v1/admin/launch/audit'),
  ]);
  hydrate(current);
  jobs.value = artwork.jobs;
  audit.value = history.entries;
}
async function save(): Promise<void> {
  if (!config.value || busy.value) return;
  const betaLaunchAt = betaDate.value ? parsePrague(betaDate.value) : null;
  const productionLaunchAt = productionDate.value ? parsePrague(productionDate.value) : null;
  if ((betaDate.value && betaLaunchAt == null) || (productionDate.value && productionLaunchAt == null)) {
    error.value = `Zadaný místní čas v pásmu ${timezone.value} neexistuje kvůli změně letního času. Vyberte jiný čas.`;
    return;
  }
  if (config.value.teaser.length > 250) { error.value = 'Krátké sdělení smí mít nejvýše 250 znaků.'; return; }
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
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Plán se nepodařilo uložit.'; }
  finally { busy.value = false; }
}
async function resetPrompt(): Promise<void> { if (config.value) config.value.personalizedLoadingPrompt = defaultPrompt; }
async function previewPrompt(): Promise<void> {
  if (!config.value || previewBusy.value) return;
  previewBusy.value = true; previewResult.value = ''; error.value = '';
  try {
      const result = await accountRequest<{ message: string }>('/app/v1/admin/launch/personalization-preview', 'POST', { name: 'Alžběta ze světa za zrcadlem', universe: 'Pohádka o říši za zrcadlem', prompt: config.value.personalizedLoadingPrompt });
    previewResult.value = result.message;
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Ukázkovou větu se nepodařilo připravit.'; }
  finally { previewBusy.value = false; }
}
async function retry(job: ArtworkJob): Promise<void> {
  if (busy.value) return;
  busy.value = true; error.value = ''; message.value = '';
  try {
    await accountRequest(`/app/v1/admin/launch/artwork/${encodeURIComponent(job.profileId)}/retry`, 'POST', { expectedRevision: job.revision });
    message.value = 'Nový pokus o přípravu portrétu byl zařazen.';
    await load();
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Nový pokus se nepodařilo zařadit.'; }
  finally { busy.value = false; }
}
let previewTimer: ReturnType<typeof setInterval> | undefined;
onMounted(() => {
  void load().catch(cause => { error.value = cause instanceof Error ? cause.message : 'Nastavení se nepodařilo načíst.'; });
  previewTimer = setInterval(() => { previewNow.value = Date.now(); }, 1000);
});
onBeforeUnmount(() => { if (previewTimer) clearInterval(previewTimer); });
</script>

<template>
  <section class="launch-admin page-stack" aria-labelledby="launch-admin-title">
    <header><h2 id="launch-admin-title">Spuštění aplikace</h2><p>Nastavte, kdy se otevře plný zážitek. Brány pro beta a produkční publikum platí společně pro web i iOS. Vydání jednotlivých distribucí se provádí samostatně.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <AwNotice v-if="message" tone="info" role="status">{{ message }}</AwNotice>
    <BetaAdmissionPanel />
    <p v-if="!config && !error" role="status">Načítání nastavení…</p>
    <template v-if="config">
      <section class="aw-card launch-admin__schedule"><h3>Čas otevření</h3><p>Časy se zadávají a zobrazují v pásmu <strong>{{ config.timeZone }}</strong>, včetně změn letního času.</p>
        <label for="launch-time-zone">Časové pásmo</label><select id="launch-time-zone" v-model="config.timeZone" class="aw-select"><option value="Europe/Prague">Europe/Prague · Praha</option><option value="UTC">UTC</option><option value="America/New_York">America/New_York · New York</option><option value="America/Los_Angeles">America/Los_Angeles · Los Angeles</option></select>
        <fieldset><legend>Beta pozvaní</legend><label><input v-model="config.betaEnabled" type="checkbox" /> Použít beta odpočítávání</label><AwField id="beta-launch" label="Otevření beta účastníkům"><input id="beta-launch" v-model="betaDate" class="aw-input" type="datetime-local" /></AwField><label><input v-model="config.betaUnlocked" type="checkbox" /> Ručně otevřít beta přístup</label><p class="launch-admin__effect">{{ config.betaUnlocked ? 'Beta účty získají plný přístup ihned po uložení.' : config.betaEnabled ? (betaDate ? 'Aktivní bude přístup od zvoleného času.' : 'Beta plán čeká na datum.') : 'Beta plán je vypnutý; beta účty zachovávají dosavadní chráněný přístup.' }}</p></fieldset>
        <fieldset><legend>Produkční publikum</legend><label><input v-model="config.productionEnabled" type="checkbox" /> Použít produkční odpočítávání</label><AwField id="production-launch" label="Otevření běžným účtům"><input id="production-launch" v-model="productionDate" class="aw-input" type="datetime-local" /></AwField><label><input v-model="config.productionUnlocked" type="checkbox" /> Ručně otevřít produkční přístup</label><p class="launch-admin__effect">{{ config.productionUnlocked ? 'Běžné účty získají plný přístup ihned po uložení.' : config.productionEnabled ? (productionDate ? 'Aktivní bude přístup od zvoleného času.' : 'Plán čeká na datum.') : 'Produkční brána je vypnutá.' }}</p></fieldset>
        <div class="launch-admin__preview"><p class="eyebrow">Živý náhled · {{ previewAudience === 'beta' ? 'beta' : 'produkce' }}</p><label for="launch-audience">Publikum</label><select id="launch-audience" v-model="previewAudience" class="aw-select"><option value="beta">Beta pozvaní</option><option value="production">Produkce</option></select><strong>{{ displayedCountdown }}</strong></div>
        <AwNotice tone="warning">Zapnutí plánu s časem v minulosti nebo ručním odemčením změní přístup hned po uložení.</AwNotice>
        <AwButton :busy="busy" @click="save">Uložit plán</AwButton>
      </section>
      <section class="aw-card"><h3>Zpráva pro pozvané</h3><label for="launch-teaser">Krátké sdělení · {{ config.teaser.length }}/250</label><textarea id="launch-teaser" v-model="config.teaser" class="aw-input" maxlength="250" rows="3" /><p class="launch-admin__preview-copy">{{ config.teaser || 'Náhled krátkého sdělení.' }}</p><AwField id="launch-loading" label="Zpráva během přípravy portrétu"><input id="launch-loading" v-model="config.initialLoadingMessage" class="aw-input" maxlength="180" /></AwField><label><input v-model="config.personalizedLoadingEnabled" type="checkbox" /> Zapnout osobní větu o postavě</label><AwField id="launch-prompt" label="Pokyn pro osobní větu"><textarea id="launch-prompt" v-model="config.personalizedLoadingPrompt" class="aw-input" rows="5" maxlength="4000" /></AwField><p>Host má cítit napětí, ne číst popis výroby. Nezmiňujte portrét, malování, rešerše ani kroky zpracování. Pokyn doplňují pevná pravidla soukromí a tajemství; ukázka spotřebuje AI prostředky.</p><div class="launch-admin__actions"><AwButton variant="secondary" @click="resetPrompt">Obnovit výchozí pokyn</AwButton><AwButton variant="secondary" :busy="previewBusy" @click="previewPrompt">Vytvořit ukázku</AwButton></div><p v-if="previewResult" class="launch-admin__preview-copy" role="status">{{ previewResult }}</p></section>
      <section class="aw-card"><h3>Příprava portrétů</h3><p v-if="!jobs.length">Žádné rozpracované portréty.</p><ul class="launch-jobs"><li v-for="job in jobs" :key="`${job.profileId}:${job.revision}`"><span><strong>{{ job.status }}</strong> · pokus {{ job.attempt }}<small v-if="job.errorCode"> · {{ job.errorCode }}</small></span><AwButton v-if="job.status === 'failed'" variant="secondary" :disabled="busy" @click="retry(job)">Opakovat přípravu</AwButton></li></ul></section>
      <section class="aw-card"><h3>Historie změn</h3><p v-if="!audit.length">Zatím bez změn.</p><ul v-else class="launch-audit"><li v-for="entry in audit" :key="entry.revision"><time :datetime="new Date(entry.changedAt).toISOString()">{{ new Intl.DateTimeFormat('cs-CZ', { dateStyle: 'medium', timeStyle: 'short', timeZone: timezone }).format(entry.changedAt) }}</time><span>{{ entry.changeSummary }}</span><small>Správce {{ entry.actorId }} · revize {{ entry.revision }}</small></li></ul></section>
      <section class="aw-card launch-admin__experience"><h3>Náhled čekárny</h3><label for="launch-stage">Stav</label><select id="launch-stage" v-model="previewStage" class="aw-select"><option v-for="(label, key) in previewStages" :key="key" :value="key">{{ label }}</option></select><div class="launch-preview-card" :class="`stage-${previewStage}`"><div class="launch-preview-art" aria-hidden="true"><img :src="curtain" alt="" width="480" height="640" /></div><div><p>Ukázka · Wand Night Stand</p><strong>{{ previewStage === 'generated' ? 'Alžběta, už tě vidíme.' : previewStage === 'failed' ? 'Něco se zpozdilo.' : 'Něco se chystá, Alžběta.' }}</strong><p>{{ config.teaser }}</p><small>{{ previewStage === 'generated' ? 'Portrét je připraven.' : previewStage === 'failed' ? 'Portrét čeká na další pokus.' : previewStage === 'enrichment' && config.personalizedLoadingEnabled ? 'Zrcadlo si vybírá správnou větu.' : config.initialLoadingMessage }}</small><span class="launch-preview-countdown">2 dny · 04 hodiny · 12 minut</span></div></div></section>
    </template>
  </section>
</template>

<style scoped>
.launch-admin{border-top:1px solid var(--aw-border-subtle);padding-top:2rem;margin-top:2rem}.launch-admin h3{margin-top:0}.launch-admin__schedule{display:grid;gap:1rem}.launch-admin__schedule fieldset{border:1px solid var(--aw-border-subtle);border-radius:.65rem;padding:1rem;display:grid;gap:.85rem}.launch-admin__schedule legend{font-weight:650;padding-inline:.4rem}.launch-admin__effect{color:var(--aw-muted);font-size:.85rem}.launch-admin__preview{display:grid;grid-template-columns:1fr auto;gap:.65rem;align-items:center;padding:1rem;border-radius:.6rem;background:var(--aw-void)}.launch-admin__preview .eyebrow{grid-column:1/-1;margin:0}.launch-admin__preview strong{grid-column:1/-1;font-size:1.4rem}.launch-admin__preview-copy{padding:1rem;border:1px solid var(--aw-border-subtle);background:var(--aw-void);color:var(--aw-muted);line-height:1.6}.launch-admin__actions{display:flex;gap:.75rem;flex-wrap:wrap}.launch-jobs,.launch-audit{list-style:none;padding:0}.launch-jobs li,.launch-audit li{display:flex;justify-content:space-between;gap:1rem;align-items:center;padding:.8rem 0;border-top:1px solid var(--aw-border-subtle)}.launch-audit li{align-items:flex-start;flex-direction:column}.launch-jobs small,.launch-audit small{color:var(--aw-muted)}.launch-preview-card{display:grid;grid-template-columns:minmax(7rem,.55fr) minmax(0,1fr);align-items:center;gap:1.5rem;margin-top:1rem;padding:1.25rem;border:1px solid var(--aw-border-subtle);border-radius:1rem;background:var(--aw-surface);max-width:46rem}.launch-preview-art{width:100%;max-width:9rem;aspect-ratio:4/5;border-radius:12px;overflow:hidden;background:#171827}.launch-preview-art img{width:100%;height:100%;object-fit:cover}.launch-preview-card>div:last-child{display:grid;gap:.6rem}.launch-preview-card strong{font-size:1.2rem}.launch-preview-card p{margin:0;color:var(--aw-muted);line-height:1.5}.launch-preview-card small{color:var(--aw-accent)}.launch-preview-countdown{font-variant-numeric:tabular-nums;font-size:.83rem;color:var(--aw-ivory)}.stage-failed .launch-preview-art{filter:grayscale(.8);opacity:.55}@media(max-width:520px){.launch-jobs li{align-items:flex-start;flex-direction:column}.launch-preview-card{grid-template-columns:1fr}.launch-preview-art{max-width:7rem}}
</style>
