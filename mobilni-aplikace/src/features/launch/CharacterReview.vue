<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { account, accountRequest } from '../identity/session';
import { customerFixedUniverse } from '../../application/launchAccess';
import { customerCopy } from '../../application/customerCopy';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
const props = defineProps<{ profileId: string; revision: number; status: string }>();
const emit = defineEmits<{ saved: [] }>();
interface Findings { recognizedName: string; world: string; summary: string; facts: { text: string }[]; conversationIdeas: string[]; ambiguities: string[]; evidenceKind?: string; sources?: { title: string; url: string }[] }
interface Profile { profileId: string; revision: number; status: string; errorCode?: string; userInput: { name: string; world: string; version?: string; description?: string; language?: string }; enrichment?: Findings }
const copy = computed(() => customerCopy.language.value === 'en' ? en : cs);
const cs = {
 title: 'Co jsme o postavě zjistili', edit: 'Upravit údaje nebo zadání', refresh: 'Načíst aktuální stav',
 name: 'Jméno postavy', world: 'Univerzum', version: 'Verze nebo období', description: 'Doplňující popis', language: 'Jazyk postavy',
 recognizedName: 'Rozpoznané jméno', summary: 'Souhrn', facts: 'Fakta', conversationIdeas: 'Náměty k rozhovoru', ambiguities: 'Nejasnosti', sources: 'Zdroje',
 input: 'Zadání pro vyhledání', lines: 'Jeden údaj na řádek. Nejvýše 6 faktů, 5 námětů a 5 nejasností, každý do 400 znaků.',
 save: 'Uložit opravené údaje', regenerate: 'Znovu připravit z upraveného zadání',
 consent: 'Souhlasím s odesláním upraveného zadání a předchozího výkladu OpenAI pro nové vyhledání a přípravu textu a portrétu.',
 manual: 'Ruční úprava uloží údaje bez volání AI. Původní citace se odstraní a starý portrét se zneplatní.',
 edited: 'Údaje upravené tebou; nejsou ověřené veřejnými zdroji.', preparing: 'Informace o postavě se připravují. Zadání můžeš upravit už teď.',
 empty: 'Zatím nemáme připravené informace. Můžeš je zadat ručně nebo upravit zadání pro nové hledání.',
 saved: 'Opravené údaje jsou uložené.', queued: 'Nová příprava byla zařazena.', diagnostic: 'Kód chyby',
 conflict: 'Postava se mezitím změnila. Načti aktuální stav před dalším uložením.',
 failed: 'Uložení se nepodařilo potvrdit. Návrh zůstává ve formuláři. Před dalším pokusem načti aktuální stav.',
 readFailed: 'Informace se nepodařilo načíst. Zkontroluj připojení a načti aktuální stav.', loading: 'Načítáme informace…',
 preparationFailed: 'Příprava informací selhala. Uprav zadání a spusť přípravu znovu, nebo údaje zadej ručně.',
};
const en: typeof cs = {
 title: 'What we found about your character', edit: 'Edit findings or input', refresh: 'Refresh current state',
 name: 'Character name', world: 'Universe', version: 'Version or period', description: 'Additional context', language: 'Character language',
 recognizedName: 'Recognized name', summary: 'Summary', facts: 'Facts', conversationIdeas: 'Conversation ideas', ambiguities: 'Uncertainties', sources: 'Sources',
 input: 'Lookup input', lines: 'One item per line. Up to 6 facts, 5 ideas and 5 uncertainties, each up to 400 characters.',
 save: 'Save corrected findings', regenerate: 'Regenerate from revised input',
 consent: 'I agree to send the revised input and previous interpretation to OpenAI for a new lookup, text and portrait.',
 manual: 'Manual edits save without calling AI. Original citations are removed and the old portrait is invalidated.',
 edited: 'Edited by you; not verified against public sources.', preparing: 'Character information is being prepared. You can already edit the input.',
 empty: 'No findings are ready yet. Enter them yourself or revise the input for a new lookup.',
 saved: 'Corrected findings saved.', queued: 'New preparation queued.', diagnostic: 'Error code',
 conflict: 'The character has changed. Refresh the current state before saving again.',
 failed: 'We could not confirm the save. Your draft is still in the form. Refresh the current state before trying again.',
 readFailed: 'We could not load the findings. Check your connection and refresh the current state.', loading: 'Loading findings…',
 preparationFailed: 'Preparing the findings failed. Revise the input and regenerate, or enter the findings yourself.',
};
const profile = ref<Profile | null>(null);
const input = ref({ name: '', world: '', version: '', description: '', language: '' });
const findings = ref({ recognizedName: '', world: '', summary: '', facts: '', conversationIdeas: '', ambiguities: '' });
const editing = ref(false), busy = ref(false), loading = ref(false), consent = ref(false), dirty = ref(false);
const error = ref(''), notice = ref('');
let epoch = 0;
let readSequence = 0;
let controller = new AbortController();
const preparing = computed(() => ['queued', 'running'].includes(profile.value?.status ?? props.status));
function seed(value: Profile): void {
 input.value = { name: value.userInput.name, world: value.userInput.world, version: value.userInput.version ?? '', description: value.userInput.description ?? '', language: value.userInput.language ?? '' };
 const f = value.enrichment;
 findings.value = { recognizedName: f?.recognizedName ?? value.userInput.name, world: f?.world ?? value.userInput.world, summary: f?.summary ?? '', facts: f?.facts?.map(x => x.text).join('\n') ?? '', conversationIdeas: f?.conversationIdeas?.join('\n') ?? '', ambiguities: f?.ambiguities?.join('\n') ?? '' };
}
async function load(): Promise<void> {
 const current = epoch, sequence = ++readSequence;
 loading.value = true; error.value = '';
 try {
  const result = await accountRequest<{ profile: Profile | null }>('/app/v1/prelaunch/character/review', 'GET', undefined, controller.signal);
  if (current !== epoch || sequence !== readSequence) return;
  if (result.profile?.profileId !== props.profileId) { error.value = copy.value.conflict; return; }
  profile.value = result.profile;
  if (result.profile.revision !== props.revision) emit('saved');
  if (!dirty.value) seed(result.profile);
 } catch { if (current === epoch && sequence === readSequence) error.value = copy.value.readFailed; }
 finally { if (current === epoch && sequence === readSequence) loading.value = false; }
}
function lines(value: string): string[] { return value.split('\n').map(x => x.trim()).filter(Boolean); }
async function save(regenerate: boolean): Promise<void> {
 if (busy.value || !profile.value || (regenerate && !consent.value)) return;
 const current = epoch;
 readSequence++; loading.value = false;
 busy.value = true; error.value = ''; notice.value = '';
 try {
  const result = await accountRequest<{ profile: Profile }>('/app/v1/prelaunch/character/review', 'PUT', {
   expectedProfileId: profile.value.profileId, expectedRevision: profile.value.revision,
   userInput: input.value, regenerate, openAiConsent: regenerate && consent.value,
   findings: regenerate ? undefined : { ...findings.value, facts: lines(findings.value.facts), conversationIdeas: lines(findings.value.conversationIdeas), ambiguities: lines(findings.value.ambiguities) },
  }, controller.signal);
  if (current !== epoch) return;
  profile.value = result.profile; dirty.value = false; consent.value = false; editing.value = false;
  seed(result.profile); notice.value = regenerate ? copy.value.queued : copy.value.saved;
  emit('saved');
 } catch (cause) {
  if (current !== epoch) return;
  const code = cause && typeof cause === 'object' && 'code' in cause ? cause.code : '';
  error.value = code === 'character_revision_conflict' ? copy.value.conflict : copy.value.failed;
 } finally { if (current === epoch) busy.value = false; }
}
watch(() => [account.value?.id, props.profileId, props.revision] as const, (next, previous) => {
 epoch++; controller.abort(); controller = new AbortController();
 const sameIdentity = previous && next[0] === previous[0] && next[1] === previous[1];
 profile.value = null; consent.value = false; busy.value = false; notice.value = '';
 if (!sameIdentity) { dirty.value = false; editing.value = false; }
 void load();
}, { immediate: true });
watch(() => props.status, () => { if (!busy.value) void load(); });
onBeforeUnmount(() => { epoch++; controller.abort(); });
</script>
<template>
 <section class="character-review">
  <AwNotice v-if="profile?.status === 'failed'" tone="warning" role="alert">{{ copy.preparationFailed }} <span v-if="profile.errorCode">{{ copy.diagnostic }}: {{ profile.errorCode }}</span></AwNotice>
  <AwNotice v-if="error" tone="warning" role="alert">{{ error }} <button type="button" class="aw-button" :disabled="busy || loading" @click="load">{{ copy.refresh }}</button></AwNotice>
  <details>
   <summary>{{ copy.title }}</summary>
   <p v-if="loading" role="status">{{ copy.loading }}</p>
   <p v-if="notice" role="status">{{ notice }}</p>
   <template v-if="profile">
    <p v-if="preparing" role="status">{{ copy.preparing }}</p>
    <template v-if="profile.enrichment && !editing">
     <h2>{{ profile.enrichment.recognizedName }}</h2><p>{{ profile.enrichment.world }}</p><p>{{ profile.enrichment.summary }}</p>
     <template v-for="field in (['facts', 'conversationIdeas', 'ambiguities'] as const)" :key="field"><h3 v-if="profile.enrichment[field].length">{{ copy[field] }}</h3><ul><li v-for="(item, index) in profile.enrichment[field]" :key="index">{{ typeof item === 'string' ? item : item.text }}</li></ul></template>
     <h3 v-if="profile.enrichment.sources?.length">{{ copy.sources }}</h3><ul><li v-for="source in profile.enrichment.sources" :key="source.url">{{ source.title }} · {{ source.url }}</li></ul>
     <p v-if="profile.enrichment.evidenceKind === 'user_edited'">{{ copy.edited }}</p>
    </template>
    <p v-else-if="!editing && !preparing">{{ copy.empty }}</p>
    <AwButton v-if="!editing" @click="editing = true">{{ copy.edit }}</AwButton>
    <form v-if="editing" class="review-form" @input="dirty = true" @submit.prevent="save(false)">
     <fieldset :disabled="busy"><h3>{{ copy.input }}</h3>
     <AwField v-for="field in (['name', 'world', 'version', 'description', 'language'] as const)" :id="`review-${field}`" :key="field" :label="copy[field]">
      <textarea v-if="field === 'description'" :id="`review-${field}`" v-model="input[field]" class="aw-input" maxlength="2000" rows="4" />
      <input v-else :id="`review-${field}`" v-model="input[field]" class="aw-input" :maxlength="field === 'name' ? 100 : field === 'language' ? 40 : 200" :required="field === 'name' || field === 'world'" :readonly="field === 'world' && !!customerFixedUniverse" />
     </AwField>
     <h3>{{ copy.title }}</h3><p>{{ copy.manual }}</p>
     <AwField v-for="field in (['recognizedName', 'world', 'summary', 'facts', 'conversationIdeas', 'ambiguities'] as const)" :id="`findings-${field}`" :key="field" :label="copy[field]" :hint="['facts', 'conversationIdeas', 'ambiguities'].includes(field) ? copy.lines : undefined">
      <textarea :id="`findings-${field}`" v-model="findings[field]" class="aw-input" :maxlength="field === 'summary' ? 1000 : field === 'recognizedName' ? 200 : field === 'world' ? 300 : 2405" rows="3" />
     </AwField>
     <AwButton type="submit" :disabled="busy" :busy="busy">{{ copy.save }}</AwButton>
     <label class="review-consent"><input v-model="consent" type="checkbox" /> {{ copy.consent }}</label>
     <AwButton type="button" :disabled="busy || !consent || !input.name.trim() || !input.world.trim()" @click="save(true)">{{ copy.regenerate }}</AwButton>
     </fieldset>
    </form>
   </template>
  </details>
 </section>
</template>
<style scoped>
.character-review { margin-block: 1.5rem; overflow-wrap: anywhere; }
summary { cursor: pointer; min-height: 44px; padding-block: .75rem; font-weight: 600; }
.review-form fieldset { border: 0; margin: 0; padding: 0; min-width: 0; display: grid; gap: 1rem; }
.review-form { display: grid; gap: 1rem; margin-block: 1rem; }
.review-form h3 { margin-block: 1rem 0; }
.review-consent { display: flex; align-items: start; gap: .75rem; }
.review-consent input { flex-shrink: 0; margin-top: .3rem; }
</style>
