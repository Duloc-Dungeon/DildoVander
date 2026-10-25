<script setup lang="ts">
import { Capacitor } from '@capacitor/core';
import { computed, nextTick, reactive, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwStatus from '../../components/AwStatus.vue';
import GeneratedWand from '../archive/GeneratedWand.vue';
import TestingKeepsakes from './TestingKeepsakes.vue';
import TestingDuels from './TestingDuels.vue';
import VirtualSuitcasePanel from './VirtualSuitcasePanel.vue';
import { useTestingSessions } from './useTestingSessions';
import { technicalDiagnostics } from '../persona-lab/client';
import { classifyLabFailure, describeLabFailure } from '../persona-lab/errors';
import { prepareLabImage } from '../persona-lab/image';
import { requestDiagnostics } from '../../application/requestDiagnostics';
import { diagnosticExportHistory, exportDiagnosticReport } from '../../application/diagnosticExport';
import { applicationBuild } from '../../application/buildIdentity';
import type { CharacterInput, InputMode } from './contracts';
const test = useTestingSessions();
const { account, permitted, sessions, characters, selected, error, errorRequestId, errorRejected, retrySeconds, notice, busy, loading, stale, inputMode, consent,
  text, outputEnabled, captureEnabled, spokenBusy, providerConfirmed, voicePhase, voiceActive, partial, turnPhase, uncertainTurn, open } = test;
const supportsDialogue = computed(() => selected.value?.session.engine === 'gpt-live-1');
const characterDeleteConfirmed = ref(false);
const longTermConsent = ref(false); const photoConsent = ref(false); const photoFile = ref<File | null>(null); const photoKeepsake = ref('');
const diagnosticExportStatus = ref(''); const diagnosticExportError = ref(''); const characterCopyStatus = ref('');
const showCharacterForm = ref(false); const editingCharacter = ref(false); const deleteConfirmed = ref(false);
const draft = reactive<CharacterInput>({ name: '', world: '', version: '', description: '', language: 'cs' });
const selectedCharacter = computed(() => {
  const detail = selected.value;
  if (detail && open.value && !detail.conversation.turns.length) {
    return detail.latestCharacter ?? characters.value.find(character => character.id === detail.session.characterId) ?? detail.character;
  }
  return detail?.character;
});
const latestCharacter = computed(() => selected.value?.latestCharacter
  ?? characters.value.find(character => character.id === selected.value?.session.characterId)
  ?? selected.value?.character ?? null);
const conversationSnapshot = computed(() => selected.value?.characterSnapshot ?? null);
const snapshotExcludesLatest = computed(() => !!latestCharacter.value && !!conversationSnapshot.value
  && (latestCharacter.value.profile.revision > conversationSnapshot.value.revision
    || (!!latestCharacter.value.profile.enrichment && !conversationSnapshot.value.enrichment)));
const characterInspection = computed(() => ({
  latestProfile: latestCharacter.value?.profile ?? null,
  conversationSnapshot: conversationSnapshot.value,
}));
type TestStep = 1 | 2 | 3 | 4;
const step = ref<TestStep>(1);
const steps = ['Sezení', 'Postava', 'Rozhovor', 'Výsledek'];
const characterPending = computed(() => ['queued', 'running'].includes(latestCharacter.value?.profile.status ?? ''));
const inputModes = [
  { value: 'text', label: 'Text — bez mikrofonu' },
  { value: 'voice', label: 'Živý mikrofon — GPT Live' },
  { value: 'hybrid', label: 'Hybrid — text a živý mikrofon' },
] as const;
const conversationReadiness = computed(() => {
  if (stale.value) return 'Obnovuji stav rozhovoru';
  if (captureEnabled.value) {
    switch (voicePhase.value) {
      case 'connecting': return 'Připojuji živý mikrofon a poskytovatele';
      case 'listening': return 'Mikrofon je aktivní — můžete mluvit';
      case 'waiting': return 'Čekám na hlasovou odpověď';
      case 'speaking': return 'DildoVander mluví — odpověď se přehrává průběžně';
    }
  }
  const phase = selected.value?.conversationOperation?.phase;
  if (phase === 'admitting') return 'Ověřuji požadavek';
  if (phase === 'preparing_context') return 'Načítám kontext postavy a rozhovoru';
  if (phase === 'connecting_provider') return 'Připojuji poskytovatele';
  if (phase === 'waiting_provider') return 'Čekám na odpověď poskytovatele';
  if (phase === 'finalizing_turn') return 'Ukládám dokončený tah';
  if (turnPhase.value === 'waiting') return 'Čekám na odpověď poskytovatele';
  if (turnPhase.value === 'finalizing') return 'Ukládám a ověřuji tah rozhovoru';
  if (uncertainTurn.value) return 'Ověřuji uloženou historii před povolením dalšího odeslání';
  if (turnPhase.value === 'recovering' || selected.value?.conversationActive) return 'Obnovuji probíhající požadavek';
  if (error.value && !errorRejected.value) return 'Požadavek selhal. Obnovte historii před dalším pokusem.';
  return 'Připraveno na další zprávu';
});
const errorMessage = computed(() => {
  if (error.value === 'account_required') return 'Přihlášení vypršelo. Přihlaste se znovu a obnovte stav testu.';
  if (error.value === 'csrf_required') return 'Ověření stránky vypršelo. Obnovte stránku a zkontrolujte stav před opakováním akce.';
  if (error.value === 'rate_limited') return 'Server dočasně omezuje počet požadavků.';
  if (/^lab_|^transport_error$/.test(error.value)) return describeLabFailure(classifyLabFailure(error.value, 0));
  if (/timeout|unavailable|testing_request_failed|Failed to fetch|NetworkError/i.test(error.value)) {
    return 'Požadavek se nepodařilo potvrdit. Obnovte stav a zkontrolujte, zda se operace dokončila.';
  }
  if (/active|busy|in_progress/i.test(error.value)) return 'Předchozí požadavek ještě probíhá. Vyčkejte a obnovte stav.';
  return error.value;
});
const errorTitle = computed(() => errorRejected.value ? 'Požadavek byl odmítnut' : stale.value ? 'Stav se nepodařilo obnovit' : 'Operace nebyla potvrzena');
async function goToStep(value: TestStep): Promise<void> {
  const sessionId = selected.value?.session.id;
  if (value >= 3 && step.value < 3 && open.value && selectedCharacter.value && !selected.value?.conversation.turns.length) {
    if (characterPending.value) return;
    await test.selectCharacter(selectedCharacter.value.id);
    if (error.value || selected.value?.session.id !== sessionId) return;
  }
  step.value = value;
  void nextTick(() => document.getElementById('testing-step-title')?.focus());
}
const providerEvents = computed(() => selected.value?.conversation.events.filter(event => event.type.startsWith('provider_') || event.type.startsWith('connection_')) ?? []);
const providerState = computed(() => {
  const events = providerEvents.value;
  const failed = [...events].reverse().find(event => ['provider_failure', 'connection_interrupted'].includes(event.type));
  const connected = [...events].reverse().find(event => event.type === 'provider_session' && event.code === 'ready');
  if (failed && (!connected || Date.parse(failed.createdAt) >= Date.parse(connected.createdAt))) return `Chyba: ${failed.code ?? failed.type}`;
  if(voiceActive.value && !captureEnabled.value){
    if(providerConfirmed.value)return 'Potvrzený poskytovatel pro aktuální psanou zprávu';
    return spokenBusy.value ? 'Psaný požadavek / přehrávání — viz potvrzené události' : 'Audio transport připravený; OpenAI se připojí po zprávě';
  }
  if(voiceActive.value && providerConfirmed.value)return 'Potvrzené živé spojení';
  if (voicePhase.value === 'connecting') return 'Navazování — čeká na potvrzení poskytovatele';
  if (connected) return voiceActive.value ? 'Potvrzené živé spojení' : 'Provedeno — viz potvrzené požadavky';
  return 'Zatím bez potvrzeného spojení';
});
function beginCharacter(edit = false): void {
  editingCharacter.value = edit; longTermConsent.value = edit ? selectedCharacter.value?.longTermConsent === true : false;
  Object.assign(draft, edit && selectedCharacter.value ? selectedCharacter.value.profile.userInput : { name: '', world: '', version: '', description: '', language: 'cs' });
  showCharacterForm.value = true;
}
async function saveCharacter(): Promise<void> {
  const saved = await test.saveCharacter({ ...draft }, editingCharacter.value ? selectedCharacter.value ?? undefined : undefined, longTermConsent.value);
  if (saved) showCharacterForm.value = false;
}
function switchMode(event: Event): void { test.changeMode((event.target as HTMLSelectElement).value as InputMode); }
function selectCharacter(event: Event): void { const id = (event.target as HTMLSelectElement).value; if (id) void test.selectCharacter(id); }
watch(() => selected.value?.session.id, () => { photoFile.value = null; photoConsent.value = false; photoKeepsake.value = ''; diagnosticExportStatus.value=''; diagnosticExportError.value=''; characterCopyStatus.value=''; showCharacterForm.value = false; deleteConfirmed.value = false; });
watch(() => selected.value?.session.id, () => {
  step.value = selected.value ? (open.value && !selected.value.conversation.turns.length ? 2 : 3) : 1;
  if (selected.value && !selected.value.character) beginCharacter();
});
async function portrait(): Promise<void> {
  if(!selected.value || !photoFile.value || !photoConsent.value || !consent.value)return;
  await test.run(async()=>{
    const sessionId=selected.value!.session.id; const file=photoFile.value!; const keepsake=selected.value!.keepsakes.find(item=>item.id===photoKeepsake.value) ?? selected.value!.keepsakes[0];
    if(!keepsake?.editions[0])throw new Error('Nejprve dokončete skutečnou generaci příběhu.');
    const image=await prepareLabImage(file);
    try{const uploaded=await test.client.photo(sessionId,keepsake.id,image.dataBase64);await test.client.portrait(sessionId,uploaded.id,keepsake.editions[0].id);await test.refreshAfterCommand();photoFile.value=null;}finally{URL.revokeObjectURL(image.previewUrl);}
  });
}
async function deleteCharacter():Promise<void>{if(!selectedCharacter.value||!characterDeleteConfirmed.value)return;await test.run(async()=>{await test.client.deleteCharacter(selectedCharacter.value!.id);await test.refreshAfterCommand();characterDeleteConfirmed.value=false;});}
function choosePhoto(event:Event):void{photoFile.value=(event.target as HTMLInputElement).files?.[0]??null;}
async function exportDiagnostics():Promise<void> {
  if(!selected.value)return;
  diagnosticExportStatus.value=''; diagnosticExportError.value='';
  const detail=selected.value;
  diagnosticExportStatus.value='exporting';
  const payload={schemaVersion:3,exportedAt:new Date().toISOString(),scope:'isolated_testing',
    app:{platform:Capacitor.getPlatform(),build:applicationBuild},
    session:{id:detail.session.id,conversationId:detail.conversation.id,engine:detail.session.engine,inputMode:detail.session.inputMode,status:detail.session.status,conversationStatus:detail.conversation.status,conversationActive:detail.conversationActive===true,operation:detail.conversationOperation ? {operationId:detail.conversationOperation.operationId,phase:detail.conversationOperation.phase,elapsedMs:detail.conversationOperation.elapsedMs} : null},
    characterEnrichment:detail.latestCharacter ? {characterId:detail.latestCharacter.id,profileId:detail.latestCharacter.profile.profileId,revision:detail.latestCharacter.profile.revision,status:detail.latestCharacter.profile.status,errorCode:detail.latestCharacter.profile.errorCode ?? null,updatedAt:detail.latestCharacter.profile.updatedAt ?? null,operationId:detail.latestCharacter.enrichmentOperationId ?? null} : null,
    requests:requestDiagnostics.value,exportEvents:diagnosticExportHistory(),provider:technicalDiagnostics(detail.conversation),
    virtualHardware:detail.hardware.events?.map(({type,code,createdAt,compartment})=>({type,code,createdAt,compartment,source:'simulation'}))};
  try {
    const result=await exportDiagnosticReport(payload,'testing-diagnostics.json');
    diagnosticExportStatus.value=result==='cancelled'?'Export byl zrušen.':result==='shared'?'Diagnostický soubor byl předán ke sdílení nebo uložení.':'Soubor JSON byl stažen.';
  } catch {
    diagnosticExportError.value='Diagnostický soubor se nepodařilo exportovat. Zkuste to znovu nebo zkontrolujte oprávnění aplikace.';
  }
}
async function copyCharacterInspection():Promise<void> {
  try { await navigator.clipboard.writeText(JSON.stringify(characterInspection.value,null,2)); characterCopyStatus.value='Strukturovaná data postavy byla zkopírována.'; }
  catch { characterCopyStatus.value='Kopírování se nezdařilo. Otevřete strukturovaná data a zkopírujte je ručně.'; }
}
function localTime(value: string): string { return new Date(value).toLocaleString(); }
</script>

<template>
  <div class="testing-sessions">
    <header class="testing-header">
      <div><h1>Testing Sessions</h1><p>Celý příběh se skutečným backendem a OpenAI. Fyzický svět nahrazuje virtuální kufr.</p></div>
      <AwStatus tone="warning">Testovací prostředí</AwStatus>
    </header>
    <AwNotice v-if="!permitted" tone="warning">Tato oblast vyžaduje účet Tester. Testovací pohled neuděluje práva k produkčním zařízením ani soukromým datům.</AwNotice>
    <template v-else>
      <nav class="testing-steps" aria-label="Kroky testu">
        <button v-for="(label, index) in steps" :key="label" type="button" :aria-current="step === index + 1 ? 'step' : undefined"
          :disabled="busy || (index > 0 && !selected) || (index > 1 && (!selectedCharacter || characterPending))" @click="goToStep((index + 1) as TestStep)">
          <span>{{ index + 1 }}</span>{{ label }}
        </button>
      </nav>
      <h2 id="testing-step-title" tabindex="-1">{{ step }}. {{ steps[step - 1] }}</h2>
      <AwNotice v-if="error && step !== 3" tone="danger" :title="errorTitle"><p>{{ errorMessage }}</p><p v-if="errorRejected">Server akci odmítl před jejím zpracováním. Počkejte alespoň {{ retrySeconds }} s před dalším pokusem.</p><small v-if="errorMessage !== error">{{ error }}</small><p v-if="errorRequestId" class="testing-code">ID požadavku: {{ errorRequestId }}</p> <AwButton :disabled="busy || retrySeconds > 0" @click="test.refresh">Obnovit stav</AwButton></AwNotice>
      <AwNotice v-if="notice" tone="info">{{ notice }}</AwNotice>
      <AwNotice v-if="stale" tone="warning">Stav je zastaralý. Obnovení spojení nevytváří nový rozhovor ani neopakuje příkazy.</AwNotice>
      <section v-show="step === 1" class="testing-setup" aria-labelledby="testing-start-title">
        <div><h2 id="testing-start-title">{{ selected ? 'Aktivní test a nové sezení' : 'Začněte bez hardwaru' }}</h2>
          <p class="aw-muted">Přihlášeni jako {{ account?.displayName }}. Testovací postavy, rozhovory a výsledky zůstávají oddělené od produkce.</p>
        </div>
        <div class="testing-start-controls">
          <div class="testing-new-session">
          <p class="aw-muted">Engine dialogu: GPT Live · gpt-live-1. Hlas DV01 spravuje server.</p>
          </div>
          <AwField id="testing-input" label="Vstup"><select id="testing-input" :value="inputMode" class="aw-select" :disabled="busy" @change="switchMode">
            <option v-for="mode in inputModes" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
          </select></AwField>

        </div>
        <label class="testing-consent"><input v-model="consent" type="checkbox" /> Souhlasím s odesláním testovací postavy, zpráv a zvoleného audia či fotografie OpenAI a s uložením historie. Skutečné požadavky čerpají placené API. Osobní produkční data do testu nevkládám.</label>
        <AwButton variant="primary" :busy="busy" :disabled="!consent || voiceActive" @click="test.start">Založit test a vybrat postavu</AwButton>
      </section>
      <label v-if="step !== 1 && !consent" class="testing-consent"><input v-model="consent" type="checkbox" /> Souhlasím s odesláním testovacích dat OpenAI a uložením historie. Požadavky čerpají placené API.</label>
      <p v-if="loading" role="status">Načítání testovacích sezení…</p>
      <div class="testing-workspace">
        <aside v-show="step === 1" class="testing-session-list" aria-labelledby="testing-history-title">
          <details :open="!selected"><summary id="testing-history-title">Vaše sezení · {{ sessions.length }}</summary>
          <p v-if="!sessions.length && !loading" class="aw-muted">Zatím žádné testy. Nové sezení připraví virtuální kufr automaticky.</p>
          <ul><li v-for="session in sessions" :key="session.id">
            <button type="button" :class="['testing-session-button', { 'is-current': selected?.session.id === session.id }]" :disabled="busy || voiceActive" :aria-current="selected?.session.id === session.id ? 'true' : undefined" @click="test.resume(session.id)">
              <strong>{{ session.status === 'open' ? 'Pokračovat v testu' : 'Prohlédnout dokončený test' }}</strong><span>{{ localTime(session.createdAt) }}</span><small>{{ session.engine }} · {{ session.status }}</small>
            </button>
          </li></ul>
          </details>
          <RouterLink v-if="selected" :to="{ path: '/persona-lab', query: { testingSession: selected.session.id } }">Otevřít Persona Lab</RouterLink>
          <p v-if="selected" class="aw-muted">Sezení zůstane uložené. Návrat přes tento seznam obnoví jeho kontext.</p>
        </aside>
        <div v-if="selected" v-show="step !== 1" class="testing-main">
          <section v-show="step === 2" class="testing-character" aria-labelledby="testing-character-title">
            <div class="testing-section-heading"><h2 id="testing-character-title">{{ selectedCharacter?.profile.userInput.name ?? 'Vyberte nebo vytvořte postavu' }}</h2>
              <AwStatus v-if="selectedCharacter">{{ selectedCharacter.profile.status }}</AwStatus>
            </div>
            <div v-if="open" class="testing-actions">
              <AwField id="testing-character-choice" label="Testovací postava"><select id="testing-character-choice" :value="selected.session.characterId ?? ''" class="aw-select" :disabled="busy || voiceActive" @change="selectCharacter">
                <option value="" disabled>Vybrat uloženou postavu</option><option v-for="character in characters" :key="character.id" :value="character.id">{{ character.profile.userInput.name }} · {{ character.profile.userInput.world }}</option>
              </select></AwField>
              <AwButton :disabled="busy || voiceActive" @click="beginCharacter()">Nová postava</AwButton>
              <AwButton v-if="selectedCharacter" :disabled="busy || voiceActive" @click="beginCharacter(true)">Upravit postavu</AwButton>
            </div>
            <form v-if="showCharacterForm && open" class="testing-character-form" @submit.prevent="saveCharacter">
              <AwField id="testing-character-name" label="Jméno"><input id="testing-character-name" v-model="draft.name" class="aw-input" maxlength="120" required :disabled="busy" /></AwField>
              <AwField id="testing-character-world" label="Svět / původ"><input id="testing-character-world" v-model="draft.world" class="aw-input" maxlength="160" required :disabled="busy" /></AwField>
              <p class="aw-muted">Stačí jméno a svět. AI podle nich dohledá příběh a vlastnosti postavy stejně jako při běžné návštěvě.</p>
              <details><summary>Doplňující údaje (nepovinné)</summary>
                <AwField id="testing-character-version" label="Verze postavy (nepovinné)"><input id="testing-character-version" v-model="draft.version" class="aw-input" maxlength="120" :disabled="busy" /></AwField>
                <AwField id="testing-character-description" label="Vlastní kontext (nepovinné)"><textarea id="testing-character-description" v-model="draft.description" class="aw-input" maxlength="1200" rows="4" :disabled="busy" /></AwField>
              </details>
              <label class="testing-consent"><input v-model="longTermConsent" type="checkbox" :disabled="busy" /> Povolit izolovanou paměť této testovací postavy při dalších návštěvách. Produkční paměť se nepoužije.</label>
              <div class="testing-actions"><AwButton type="submit" variant="primary" :busy="busy" :disabled="!consent">Uložit a dohledat postavu</AwButton><AwButton :disabled="busy" @click="showCharacterForm = false">Zrušit úpravu</AwButton></div>
            </form>
            <template v-if="selectedCharacter">
              <p>{{ selectedCharacter.profile.userInput.description }}</p>
              <p v-if="characterPending" role="status">AI dohledává postavu podle jména a světa. Můžete počkat zde; stav se průběžně obnovuje.</p>
              <p v-if="selectedCharacter.profile.enrichment">{{ selectedCharacter.profile.enrichment.summary }}</p>
              <AwNotice v-if="selectedCharacter.profile.errorCode" tone="danger">{{ selectedCharacter.profile.errorCode }}</AwNotice>
              <p v-if="selectedCharacter.profile.errorCode" class="aw-muted">Postava je uložená. Dohledání můžete zopakovat nebo pokračovat se zadanými údaji.</p>
              <details><summary>Kontext a AI obohacení postavy</summary>
                <section aria-label="Původní vstup postavy">
                  <h3>Původní vstup</h3>
                  <dl class="testing-identifiers"><div><dt>Jméno</dt><dd>{{ latestCharacter?.profile.userInput.name }}</dd></div><div><dt>Svět / původ</dt><dd>{{ latestCharacter?.profile.userInput.world }}</dd></div><div><dt>Verze</dt><dd>{{ latestCharacter?.profile.userInput.version || 'Neuvedena' }}</dd></div><div><dt>Popis a dodaný kontext</dt><dd>{{ latestCharacter?.profile.userInput.description || 'Neuveden' }}</dd></div></dl>
                </section>
                <section aria-label="Aktuální AI obohacení">
                  <h3>Aktuální uložené AI obohacení · revize {{ latestCharacter?.profile.revision }}</h3>
                  <p>Stav: {{ latestCharacter?.profile.status ?? 'neznámý' }}</p>
                  <template v-if="latestCharacter?.profile.enrichment"><p><strong>Rozpoznané jméno:</strong> {{ latestCharacter.profile.enrichment.recognizedName }}</p><p><strong>Rozpoznaný svět:</strong> {{ latestCharacter.profile.enrichment.world || 'Neuveden' }}</p><p><strong>Souhrn:</strong> {{ latestCharacter.profile.enrichment.summary }}</p><p><strong>Model, verze pokynu a důkazy:</strong> {{ latestCharacter.profile.enrichment.model }} · {{ latestCharacter.profile.enrichment.promptVersion || 'neuvedena' }} · {{ latestCharacter.profile.enrichment.evidenceKind }}</p><p v-if="latestCharacter.profile.enrichment.processedAt">Dokončeno: {{ new Date(latestCharacter.profile.enrichment.processedAt * 1000).toLocaleString() }} · vstupní revize {{ latestCharacter.profile.enrichment.inputRevision }}</p><p v-if="latestCharacter.profile.enrichment.needsClarification">AI potřebuje upřesnění.</p><h4>Fakta a zdroje</h4><ul><li v-for="fact in latestCharacter.profile.enrichment.facts ?? []" :key="fact.text">{{ fact.text }}<span v-for="url in fact.sourceUrls" :key="url"> · <a :href="url" target="_blank" rel="noopener noreferrer">{{ url }}</a></span></li></ul><ul><li v-for="source in latestCharacter.profile.enrichment.sources ?? []" :key="source.url"><a :href="source.url" target="_blank" rel="noopener noreferrer">{{ source.title }}</a></li></ul><h4>Náměty k rozhovoru</h4><ul><li v-for="idea in latestCharacter.profile.enrichment.conversationIdeas" :key="idea">{{ idea }}</li></ul><h4>Nejasnosti</h4><ul><li v-for="ambiguity in latestCharacter.profile.enrichment.ambiguities" :key="ambiguity">{{ ambiguity }}</li></ul></template>
                  <AwNotice v-else-if="latestCharacter?.profile.errorCode" tone="danger">Obohacení selhalo: {{ latestCharacter.profile.errorCode }}. Zadaná postava zůstává uložená.</AwNotice>
                  <p v-else class="aw-muted">Obohacení není dokončeno. Stav: {{ latestCharacter?.profile.status ?? 'nenačteno' }}.</p>
                </section>
                <section aria-label="Neměnný snímek konverzace">
                  <h3>Kontext zmrazený při zahájení rozhovoru</h3>
                  <template v-if="conversationSnapshot">
                    <p>Profil {{ conversationSnapshot.profileId }}, revize {{ conversationSnapshot.revision }} · zachyceno {{ new Date(conversationSnapshot.capturedAt * 1000).toLocaleString() }}.</p>
                    <p>Jméno: {{ conversationSnapshot.userInput.name }} · svět: {{ conversationSnapshot.userInput.world }}</p>
                    <p>Obohacení při zahájení: {{ conversationSnapshot.enrichment ? 'bylo dostupné' : 'nebylo dostupné' }}.</p>
                    <p>Dostupná pole při zahájení: jméno, svět<template v-if="conversationSnapshot.userInput.version">, verze</template><template v-if="conversationSnapshot.userInput.description">, dodaný popis</template><template v-if="conversationSnapshot.enrichment">, obohacený profil</template>.</p>
                    <details v-if="conversationSnapshot.context"><summary>Skutečný kontext předaný rozhovoru</summary><pre class="testing-json">{{ conversationSnapshot.context }}</pre></details>
                    <p v-if="snapshotExcludesLatest" class="aw-muted">Existuje novější verze profilu nebo obohacení. Tento rozhovor ji nepoužívá; jeho kontext zůstává neměnný.</p>
                  </template>
                  <p v-else class="aw-muted">Backend neposkytl uložená metadata snímku konverzace.</p>
                </section>
                <details><summary>Strukturovaná data uložená backendem</summary><pre class="testing-json">{{ JSON.stringify(characterInspection, null, 2) }}</pre><AwButton @click="copyCharacterInspection">Kopírovat JSON postavy</AwButton><p v-if="characterCopyStatus" role="status">{{ characterCopyStatus }}</p></details>
                <AwButton v-if="open" :disabled="busy || !consent || voiceActive || characterPending" @click="test.enrich">Spustit skutečné AI obohacení</AwButton>
              </details>
            </template>
            <AwButton v-if="selectedCharacter && !showCharacterForm" variant="primary" :disabled="busy || (characterPending && !selected?.conversation.turns.length)" @click="goToStep(3)">Pokračovat k rozhovoru</AwButton>
          </section>
          <section v-show="step === 3" class="testing-conversation" aria-labelledby="testing-conversation-title">
            <div class="testing-section-heading"><h2 id="testing-conversation-title">Rozhovor s DildoVanderem</h2><AwStatus>{{ inputMode }}</AwStatus></div>
            <p v-if="selectedCharacter" class="aw-muted">{{ selectedCharacter.profile.userInput.name }} · {{ selectedCharacter.profile.userInput.world }}</p>
            <AwNotice v-if="!supportsDialogue" tone="info">Toto historické sezení GPT Realtime je dostupné ke čtení. Nové testy a odpovědi používají pouze GPT Live.</AwNotice>
            <AwField v-if="open && supportsDialogue" id="testing-conversation-input" label="Režim rozhovoru">
              <select id="testing-conversation-input" :value="inputMode" class="aw-select" :disabled="busy || uncertainTurn || (selected.conversationActive && !voiceActive)" @change="switchMode">
                <option v-for="mode in inputModes" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
              </select>
            </AwField>
            <AwNotice v-if="error" tone="danger" :title="errorTitle"><p>{{ errorMessage }}</p><p v-if="errorRejected">Server akci odmítl před jejím zpracováním. Počkejte alespoň {{ retrySeconds }} s před dalším pokusem.</p><small v-if="errorMessage !== error">{{ error }}</small><p v-if="errorRequestId" class="testing-code">ID požadavku: {{ errorRequestId }}</p><p>Zpráva se sama znovu neodešle.</p><AwButton :disabled="busy || retrySeconds > 0" @click="test.refresh">Obnovit stav</AwButton></AwNotice>
            <p role="status" aria-live="polite">{{ conversationReadiness }}<span v-if="selected.conversationOperation"> · {{ selected.conversationOperation.elapsedMs }} ms · operace {{ selected.conversationOperation.operationId }}</span></p>
            <p v-if="!selected.conversation.turns.length" class="aw-muted">{{ inputMode === 'text' ? 'Napište první zprávu. Textový režim nikdy nevyžaduje mikrofon.' : 'Zapněte mikrofon a začněte mluvit. Spojení zůstane otevřené pro další odpovědi.' }}</p>
            <ol class="testing-chat" aria-label="Uložený rozhovor"><li v-for="turn in selected.conversation.turns" :key="turn.id" :class="`testing-turn-${turn.role}`">
              <strong>{{ turn.role === 'user' ? selectedCharacter?.profile.userInput.name ?? 'Vy' : 'DildoVander' }}</strong><p>{{ turn.text || 'Text není uložený.' }}</p><small v-if="turn.latencyMs != null">{{ turn.latencyMs }} ms · skutečný backend</small>
            </li></ol>
            <p v-if="partial.user" class="testing-partial">Vy · {{ partial.user }}</p><p v-if="partial.assistant" class="testing-partial">DildoVander · {{ partial.assistant }}</p>
            <form v-if="open && supportsDialogue && inputMode !== 'voice'" class="testing-chat-form" @submit.prevent="test.send">
              <p v-if="inputMode === 'hybrid'" class="aw-muted">Odeslání psané zprávy vypne mikrofon. Pro pokračování hlasem ho znovu zapněte tlačítkem.</p>
              <AwField id="testing-message" label="Vaše zpráva"><textarea id="testing-message" v-model="text" class="aw-input" rows="3" maxlength="4000" :disabled="!selectedCharacter || (voiceActive && captureEnabled && inputMode !== 'hybrid')" /></AwField>
              <AwButton type="submit" variant="primary" :busy="busy || spokenBusy" :disabled="busy || uncertainTurn || !text.trim() || !selectedCharacter || !consent || stale || (selected.conversationActive && !voiceActive)">Odeslat skutečnému AI</AwButton>
            </form>
            <label v-if="open && supportsDialogue" class="testing-consent"><input v-model="outputEnabled" type="checkbox" /> Přehrávat skutečný hlas asistenta (také pro psané zprávy, bez mikrofonu)</label>
            <template v-if="open && supportsDialogue && inputMode !== 'text'">
              <p class="aw-muted">GPT Live · živý rozhovor s průběžným přenosem mikrofonu, hlasové odpovědi a přepisu. Mikrofon i přehrávání se zapnou až tlačítkem. Přepnutí do textu uzavře audio spojení a zachová uloženou historii.</p>

              <div class="testing-actions"><AwButton v-if="!voiceActive" variant="primary" :disabled="busy || uncertainTurn || selected.conversationActive || !selectedCharacter || !consent || stale" @click="test.startVoice">Zapnout mikrofon a hlasové spojení</AwButton>
                <AwButton v-if="voiceActive" @click="test.interrupt">Přerušit odpověď</AwButton><AwButton v-if="voiceActive" @click="test.stopVoice">{{ captureEnabled ? 'Vypnout mikrofon' : 'Odpojit hlasový výstup' }}</AwButton></div>
            </template>
            <p v-if="inputMode === 'text'" class="aw-muted">Mikrofon se neaktivuje. Se zapnutým hlasovým výstupem zpráva použije stejný skutečný model a jeho audio spojení; po přepnutí se obnoví skutečně uložená historie.</p><div v-if="inputMode === 'text' && voiceActive" class="testing-actions"><AwButton @click="test.interrupt">Přerušit hlasovou odpověď</AwButton><AwButton @click="test.stopVoice">Odpojit hlasový výstup</AwButton></div>
            <AwButton :disabled="busy || spokenBusy" @click="goToStep(4)">Pokračovat k výsledku</AwButton>
          </section>
          <div v-show="step === 4"><VirtualSuitcasePanel :hardware="selected.hardware" :device-id="selected.session.virtualDeviceId" :stale="stale" /></div>
          <section v-show="step === 4" class="testing-results" aria-labelledby="testing-results-title">
            <h2 id="testing-results-title">Výsledky, paměť a památky</h2>
            <GeneratedWand v-if="selected.wand?.selectedWand" :wand="selected.wand.selectedWand" :show-name="true" :show-artwork="false" />
            <p v-else class="aw-muted">Zatím žádná potvrzená hůlka. Výsledek se objeví až po skutečném rozhodnutí aplikace.</p>
            <p v-if="selected.wand">Generování hůlek: {{ selected.wand.status }} <span v-if="selected.wand.errorCode">· {{ selected.wand.errorCode }}</span></p>
            <div v-if="open" class="testing-actions"><AwButton :busy="busy" :disabled="!consent || !selectedCharacter || voiceActive || stale" @click="test.selectWand">Vybrat hůlku skutečným AI</AwButton><AwButton :busy="busy" :disabled="!consent || !selected.wand?.selectedWand || voiceActive || stale" @click="test.generate">Generovat skutečné obrazy a památku</AwButton></div>
            <TestingKeepsakes @refresh="test.refresh" :key="selected.session.id" :session-id="selected.session.id" :items="selected.keepsakes" :client="test.client" />
            <details v-if="open && selected.keepsakes.some(item=>item.editions.length)"><summary>Fotografie místo kamery a skutečný AI portrét</summary><p class="aw-muted">Vyberte obrázek. Příprava odstraní metadata a zmenší snímek. Nenahrává se před potvrzením.</p><AwField id="testing-photo" label="Fotografie pro portrét"><input id="testing-photo" type="file" accept="image/jpeg,image/png,image/webp" :disabled="busy || selected.hardware.cameraAvailable === false" @change="choosePhoto" /></AwField><label class="testing-consent"><input v-model="photoConsent" type="checkbox" :disabled="busy" /> Souhlasím se soukromým uložením fotografie a jejím zpracováním OpenAI pro portrét v tomto testovacím sezení.</label><AwButton :busy="busy" :disabled="!photoFile || !photoConsent || !consent || selected.hardware.cameraAvailable === false" @click="portrait">Nahrát a generovat skutečný portrét</AwButton></details>
            <p v-if="!selected.keepsakes.length" class="aw-muted">Zatím žádné uložené testovací památky.</p>
            <details><summary>Uložená testovací paměť</summary><pre class="testing-json">{{ JSON.stringify(selected.memory, null, 2) }}</pre></details>
          </section>
          <div v-show="step === 4"><TestingDuels :key="selected.session.id" :session-id="selected.session.id" :sessions="sessions" :characters="characters" :history="selected.duels" :client="test.client" :enabled="!busy && !stale && consent" /></div>
          <section class="testing-lifecycle" aria-label="Řízení testovacího sezení">
            <div class="testing-actions"><AwButton v-if="open" variant="danger" @click="test.cancel">Stop / zrušit probíhající operaci</AwButton><AwButton v-if="open" :disabled="busy" @click="test.end">Ukončit a uložit sezení</AwButton><AwButton :disabled="busy" @click="test.refresh">Obnovit stav</AwButton></div>
            <details><summary>Smazat testovací data tohoto sezení</summary><label class="testing-consent"><input v-model="deleteConfirmed" type="checkbox" /> Trvale smazat toto testovací sezení. Produkční data nejsou cílem této akce.</label><AwButton variant="danger" :disabled="!deleteConfirmed || busy || voiceActive" @click="test.remove">Smazat testovací sezení</AwButton></details>
          </section>
          <details v-if="selectedCharacter"><summary>Smazat tuto testovací postavu a její testovací paměť</summary><label class="testing-consent"><input v-model="characterDeleteConfirmed" type="checkbox" /> Smazat postavu {{ selectedCharacter.profile.userInput.name }} a její izolovanou paměť. Propojená testovací sezení se ukončí.</label><AwButton variant="danger" :disabled="!characterDeleteConfirmed || busy || voiceActive" @click="deleteCharacter">Smazat testovací postavu</AwButton></details>
          <details class="testing-diagnostics"><summary>Diagnostika skutečných služeb a virtuálního hardwaru</summary>
            <div class="testing-state-strip" aria-label="Zdroje stavu">
              <div><span>Backend / API</span><strong>{{ stale ? 'Nedostupný — historická data' : 'Odpověď skutečného backendu' }}</strong></div>
              <div><span>OpenAI</span><strong>{{ providerState }}</strong></div>
              <div><span>Hardware</span><strong>Virtuální — žádné ESP32 ani BLE</strong></div>
              <div><span>Rozhovor</span><strong>{{ voiceActive ? voicePhase : selected.conversation.status }} · {{ selected.session.engine }}</strong></div>
            </div>
            <details v-if="open"><summary>Vložit poruchu pouze do virtuálního kufru</summary><p class="aw-muted">Výslovně vložené chyby simulace; neoznačují výpadek skutečného OpenAI ani backendu.</p><div class="testing-actions"><AwButton :disabled="busy" @click="test.fault('disconnect')">Simulovat odpojení</AwButton><AwButton :disabled="busy" @click="test.fault('servo_failure')">Simulovat chybu serva</AwButton><AwButton :disabled="busy" @click="test.fault('missing_camera')">Simulovat chybějící kameru</AwButton><AwButton :disabled="busy" @click="test.fault('recover')">Zotavit virtuální kufr</AwButton></div></details>
            <div class="testing-actions"><AwButton :busy="diagnosticExportStatus === 'exporting'" :disabled="diagnosticExportStatus === 'exporting'" @click="exportDiagnostics">Exportovat bezpečné diagnostiky</AwButton></div><p v-if="diagnosticExportStatus" role="status">{{ diagnosticExportStatus }}</p><AwNotice v-if="diagnosticExportError" tone="danger">{{ diagnosticExportError }}</AwNotice><p class="aw-muted">Export neobsahuje text rozhovoru, obrázky, osobní paměť ani přístupové údaje.</p>
            <dl class="testing-identifiers"><div><dt>Účet</dt><dd>{{ account?.id }}</dd></div><div><dt>Testovací sezení</dt><dd>{{ selected.session.id }}</dd></div><div><dt>Rozhovor</dt><dd>{{ selected.conversation.id }}</dd></div><div><dt>Model</dt><dd>{{ selected.session.engine }}</dd></div></dl>
            <p class="aw-muted">Události níže pocházejí z backendu. Přijetí HTTP požadavku samo nepotvrzuje volání OpenAI ani fyzický pohyb.</p>
            <ol class="testing-timeline"><li v-for="event in selected.conversation.events" :key="event.id"><strong>{{ event.type }}</strong> · {{ event.code }}<small>{{ localTime(event.createdAt) }}<template v-if="event.latencyMs != null"> · {{ event.latencyMs }} ms</template><template v-if="event.inputTokens != null"> · {{ event.inputTokens }} vstupních tokenů</template><template v-if="event.outputTokens != null"> · {{ event.outputTokens }} výstupních tokenů</template><template v-if="event.costMicroUsd != null"> · {{ event.costMicroUsd }} µUSD</template></small><span v-if="event.providerSessionId" class="testing-code">Provider session: {{ event.providerSessionId }}</span></li></ol>
          </details>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped src="./testing-sessions.css"></style>
