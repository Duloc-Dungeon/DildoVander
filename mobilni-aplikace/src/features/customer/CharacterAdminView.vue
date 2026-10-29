<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AdminReauthentication from '../identity/AdminReauthentication.vue';
import AdminNavigation from '../identity/AdminNavigation.vue';
import AdminConfirmation from '../identity/AdminConfirmation.vue';
import characterIcon from '../../assets/wns-v2/icons/regular/identification-card.svg';
import { account, accountRequest, accountTransport } from '../identity/session';
import {
  adminCharacterPath, characterAdminError, characterImageTypes, maxCharacterImageBytes,
  uploadCharacterPortrait, type AdminCharacter, type AdminCharacterSummary, type AdminCharacterPage,
} from './characterAdministration';

const characters = ref<AdminCharacterSummary[]>([]);
const nextCursor = ref<string | null>(null);
const selected = ref<AdminCharacter | null>(null);
const search = ref('');
const loading = ref(false);
const reading = ref(false);
const saving = ref(false);
const listError = ref('');
const error = ref('');
const message = ref('');
const needsReauthentication = ref(false);
const conflict = ref(false);
const portraitUrl = ref('');
const portraitFailed = ref(false);
const portraitLoading = ref(false);
const replacement = ref<File | null>(null);
const replacementUrl = ref('');
const fileInput = ref<HTMLInputElement>();
const editorHeading = ref<HTMLElement>();
const draft = reactive({ name: '', world: '', version: '', description: '', language: '' });
let generation = 0;
let listController: AbortController | undefined;
let detailController: AbortController | undefined;
let portraitController: AbortController | undefined;
let writeController: AbortController | undefined;

const allowed = computed(() => account.value?.roles.some(role => ['Owner', 'Administrator'].includes(role)) ?? false);
const input = () => ({
  name: draft.name.trim(), world: draft.world.trim(), version: draft.version.trim() || null,
  description: draft.description.trim() || null, language: draft.language.trim() || null,
});
const dirty = computed(() => {
  const original = selected.value?.userInput;
  return !!original && JSON.stringify(input()) !== JSON.stringify({
    name: original.name.trim(), world: original.world.trim(), version: original.version?.trim() || null,
    description: original.description?.trim() || null, language: original.language?.trim() || null,
  });
});
const filtered = computed(() => {
  const query = search.value.trim().toLocaleLowerCase('cs');
  return characters.value.filter(item => `${item.userInput.name} ${item.userInput.world}`.toLocaleLowerCase('cs').includes(query));
});
const statusLabels: Record<string, string> = {
  saved: 'Uloženo', queued: 'Čeká na přípravu', running: 'Příprava probíhá', ready: 'Připraveno',
  needs_clarification: 'Vyžaduje upřesnění', failed: 'Příprava selhala',
};
const statusLabel = (status: string) => statusLabels[status] ?? 'Stav není dostupný';

function clearReplacement(): void {
  if (replacementUrl.value) URL.revokeObjectURL(replacementUrl.value);
  replacementUrl.value = '';
  replacement.value = null;
  if (fileInput.value) fileInput.value.value = '';
}

function clearPortrait(): void {
  portraitController?.abort();
  if (portraitUrl.value) URL.revokeObjectURL(portraitUrl.value);
  portraitUrl.value = '';
  portraitFailed.value = false;
  portraitLoading.value = false;
}

async function loadPortrait(character: AdminCharacter): Promise<void> {
  clearPortrait();
  if (!character.portrait?.hasImage) return;
  const current = generation;
  const controller = portraitController = new AbortController();
  portraitLoading.value = true;
  try {
    const response = await accountTransport(`${adminCharacterPath(character.profileId)}/portrait?revision=${character.revision}`, { signal: controller.signal });
    if (!response.ok) throw new Error('portrait_unavailable');
    const blob = await response.blob();
    if (controller.signal.aborted || current !== generation) return;
    if (!characterImageTypes.includes(blob.type) || !blob.size || blob.size > maxCharacterImageBytes) throw new Error('portrait_invalid');
    portraitUrl.value = URL.createObjectURL(blob);
  } catch {
    if (!controller.signal.aborted && current === generation) portraitFailed.value = true;
  } finally {
    if (!controller.signal.aborted && current === generation) portraitLoading.value = false;
  }
}

function accept(character: AdminCharacter): void {
  selected.value = character;
  Object.assign(draft, {
    name: character.userInput.name, world: character.userInput.world, version: character.userInput.version ?? '',
    description: character.userInput.description ?? '', language: character.userInput.language ?? '',
  });
  characters.value = characters.value.map(item => item.profileId === character.profileId ? character : item);
  conflict.value = false;
  clearReplacement();
  void loadPortrait(character);
}

async function loadList(more = false): Promise<void> {
  if (!allowed.value || saving.value) return;
  if (more && (!nextCursor.value || loading.value)) return;
  listController?.abort();
  const controller = listController = new AbortController();
  const current = generation;
  loading.value = true;
  listError.value = '';
  try {
    const path = `/app/v1/admin/characters${more ? `?after=${encodeURIComponent(nextCursor.value!)}` : ''}`;
    const result = await accountRequest<AdminCharacterPage>(path, 'GET', undefined, controller.signal);
    if (controller.signal.aborted || current !== generation) return;
    characters.value = more ? [...characters.value, ...result.items] : result.items;
    nextCursor.value = result.nextCursor ?? null;
  } catch {
    if (!controller.signal.aborted && current === generation) listError.value = 'Seznam postav se nepodařilo načíst. Zkuste jej obnovit.';
  } finally {
    if (!controller.signal.aborted && current === generation) loading.value = false;
  }
}

async function selectCharacter(profileId: string): Promise<void> {
  if (saving.value || !allowed.value) return;
  if ((dirty.value || replacement.value) && !window.confirm('Zahodit neuložené změny a načíst postavu?')) return;
  detailController?.abort();
  const controller = detailController = new AbortController();
  const current = generation;
  reading.value = true;
  error.value = ''; message.value = ''; needsReauthentication.value = false;
  try {
    const character = await accountRequest<AdminCharacter>(adminCharacterPath(profileId), 'GET', undefined, controller.signal);
    if (controller.signal.aborted || current !== generation) return;
    accept(character);
    editorHeading.value?.focus();
  } catch (cause) {
    if (!controller.signal.aborted && current === generation) error.value = characterAdminError(cause);
  } finally {
    if (!controller.signal.aborted && current === generation) reading.value = false;
  }
}

function showWriteError(cause: unknown): void {
  const code = (cause as { code?: string })?.code;
  if (code === 'confirmation_cancelled') return;
  needsReauthentication.value = code === 'recent_passkey_required';
  conflict.value = code === 'character_revision_conflict';
  error.value = characterAdminError(cause);
}

async function write(kind: 'details' | 'portrait'): Promise<void> {
  const character = selected.value;
  if (!character || saving.value || reading.value || conflict.value || !allowed.value) return;
  const file = replacement.value;
  if (kind === 'portrait' && (!file || dirty.value)) return;
  if (kind === 'portrait' && !window.confirm(`Nahradit obrázek postavy ${character.userInput.name}?`)) return;
  const current = generation;
  const controller = writeController = new AbortController();
  saving.value = true; error.value = ''; message.value = '';
  try {
    const result = kind === 'portrait'
      ? await uploadCharacterPortrait(character, file!, controller.signal)
      : await accountRequest<AdminCharacter>(adminCharacterPath(character.profileId), 'PUT', {
        expectedRevision: character.revision, userInput: input(),
      }, controller.signal, `Uložit úpravy postavy ${character.userInput.name}?`);
    if (controller.signal.aborted || current !== generation) return;
    accept(result);
    needsReauthentication.value = false;
    message.value = kind === 'portrait' ? 'Náhradní obrázek byl uložen.' : 'Údaje postavy byly uloženy.';
  } catch (cause) {
    if (!controller.signal.aborted && current === generation) showWriteError(cause);
  } finally {
    if (!controller.signal.aborted && current === generation) saving.value = false;
  }
}

function chooseImage(event: Event): void {
  const file = (event.target as HTMLInputElement).files?.[0];
  clearReplacement();
  error.value = ''; message.value = '';
  if (!file) return;
  if (!characterImageTypes.includes(file.type) || !file.size || file.size > maxCharacterImageBytes) {
    error.value = 'Vyberte neprázdný obrázek PNG, JPEG nebo WebP do 8 MB.';
    return;
  }
  replacement.value = file;
  replacementUrl.value = URL.createObjectURL(file);
}

function invalidPreview(): void {
  clearReplacement();
  error.value = 'Obrázek se nepodařilo zobrazit. Vyberte jiný soubor.';
}

function reset(): void {
  generation++;
  listController?.abort(); detailController?.abort(); writeController?.abort();
  clearPortrait(); clearReplacement();
  characters.value = []; nextCursor.value = null; selected.value = null; search.value = '';
  Object.assign(draft, { name: '', world: '', version: '', description: '', language: '' });
  loading.value = false; reading.value = false; saving.value = false;
  error.value = ''; listError.value = ''; message.value = ''; conflict.value = false; needsReauthentication.value = false;
}
watch(() => `${account.value?.id}:${account.value?.roles.join(',')}`, () => { reset(); void loadList(); }, { immediate: true, flush: 'sync' });
onBeforeUnmount(reset);
</script>

<template>
  <section class="page-stack character-admin" aria-labelledby="character-admin-title">
    <header><h1 id="character-admin-title">Postavy a portréty</h1><p>Upravte údaje postavy nebo nahrajte náhradní obrázek. Uložení nespouští generování AI; portréty zůstávají soukromé.</p></header>
    <AwNotice v-if="!allowed" tone="warning">Správa postav je dostupná vlastníkovi a správci.</AwNotice>
    <template v-else>
      <AdminNavigation current="characters" />
      <div class="character-admin__toolbar">
        <AwField id="character-search" label="Hledat podle jména nebo světa" hint="Hledání prochází načtené postavy. Další postavy načtete tlačítkem pod seznamem."><input id="character-search" v-model="search" type="search" class="aw-input" aria-describedby="character-search-hint" /></AwField>
        <AwButton :busy="loading" :disabled="saving" @click="loadList()">Obnovit seznam</AwButton>
      </div>
      <AwNotice v-if="listError" tone="warning" role="alert">{{ listError }}</AwNotice>
      <AwNotice v-if="error" tone="warning" role="alert">{{ error }}</AwNotice>
      <AdminReauthentication v-if="needsReauthentication" @verified="needsReauthentication = false; error = ''" />
      <AdminConfirmation v-if="message">{{ message }}</AdminConfirmation>
      <p v-if="loading" role="status">Načítám postavy…</p>
      <p v-if="reading" role="status">Načítám detail postavy…</p>
      <div class="character-admin__layout">
        <section class="aw-card character-admin__list" aria-label="Seznam postav">
          <h2>Postavy <small>({{ filtered.length }})</small></h2>
          <p v-if="!loading && !characters.length && !listError">Zatím tu nejsou žádné postavy.</p>
          <div v-else-if="!filtered.length && characters.length" class="character-admin__empty">
            <p>Hledání neodpovídá žádná postava.</p>
            <AwButton @click="search = ''">Zrušit hledání</AwButton>
          </div>
          <ul>
            <li v-for="character in filtered" :key="character.profileId">
              <button type="button" :aria-pressed="selected?.profileId === character.profileId" :disabled="saving || reading" @click="selectCharacter(character.profileId)">
                <strong>{{ character.userInput.name }}</strong><span>{{ character.userInput.world }}</span><small>{{ statusLabel(character.status) }} · revize {{ character.revision }}</small>
              </button>
            </li>
          </ul>
          <AwButton v-if="nextCursor" :busy="loading" :disabled="saving" @click="loadList(true)">Načíst další postavy</AwButton>
        </section>
        <section class="aw-card character-admin__editor" aria-labelledby="character-detail-title" :aria-busy="reading || saving">
          <h2 id="character-detail-title" ref="editorHeading" tabindex="-1">{{ selected ? selected.userInput.name : 'Detail postavy' }}</h2>
          <div v-if="!selected" class="character-admin__empty character-admin__casting">
            <img :src="characterIcon" alt="" width="48" height="48" />
            <p>Vyberte postavu ze seznamu.</p>
            <p class="character-admin__aside">I největší hrdina je tu vedený pod svým jménem.</p>
          </div>
          <template v-else>
            <p class="character-admin__meta">{{ statusLabel(selected.status) }} · revize {{ selected.revision }} · {{ new Date(selected.updatedAt * 1000).toLocaleString('cs') }}</p>
            <AwButton :disabled="saving || reading" @click="selectCharacter(selected.profileId)">Načíst aktuální verzi</AwButton>
            <form @submit.prevent="write('details')">
              <fieldset :disabled="saving || reading || conflict">
                <legend>Údaje postavy</legend>
                <AwField id="admin-character-name" label="Jméno postavy"><input id="admin-character-name" v-model="draft.name" class="aw-input" required maxlength="100" /></AwField>
                <AwField id="admin-character-world" label="Svět"><input id="admin-character-world" v-model="draft.world" class="aw-input" required maxlength="200" /></AwField>
                <AwField id="admin-character-version" label="Verze nebo ztvárnění"><input id="admin-character-version" v-model="draft.version" class="aw-input" maxlength="200" /></AwField>
                <AwField id="admin-character-description" label="Popis"><textarea id="admin-character-description" v-model="draft.description" class="aw-input" rows="4" maxlength="2000" /></AwField>
                <AwField id="admin-character-language" label="Jazyk"><input id="admin-character-language" v-model="draft.language" class="aw-input" maxlength="40" /></AwField>
                <p class="character-admin__meta">Změna údajů zruší rozpracovanou přípravu a původní automatický popis. Hotový obrázek zůstane zachován.</p>
                <AwButton type="submit" variant="primary" :busy="saving" :disabled="!dirty || !draft.name.trim() || !draft.world.trim()">Uložit údaje</AwButton>
              </fieldset>
            </form>
            <section class="character-admin__portrait" aria-labelledby="character-portrait-title">
              <h3 id="character-portrait-title">Portrét postavy</h3>
              <p v-if="portraitLoading" role="status">Načítám obrázek…</p>
              <img v-if="portraitUrl && !portraitFailed" :src="portraitUrl" :alt="`Současný portrét postavy ${selected.userInput.name}`" @error="portraitFailed = true" />
              <template v-else-if="portraitFailed"><p role="alert">Současný obrázek se nepodařilo načíst.</p><AwButton :disabled="saving" @click="loadPortrait(selected)">Znovu načíst obrázek</AwButton></template>
              <p v-else-if="!portraitLoading">{{ selected.portrait ? `Obrázek není k dispozici. ${statusLabel(selected.portrait.status)}.` : 'Postava zatím nemá obrázek.' }}</p>
              <fieldset :disabled="saving || reading || conflict">
                <legend>Náhradní obrázek</legend>
                <AwField id="admin-character-image" label="Vybrat obrázek" hint="PNG, JPEG nebo WebP do 8 MB. Nejvýše 4096 bodů na stranu a 16 milionů bodů celkem."><input id="admin-character-image" ref="fileInput" type="file" accept="image/png,image/jpeg,image/webp" aria-describedby="admin-character-image-hint" @change="chooseImage" /></AwField>
                <template v-if="replacement">
                  <p>{{ replacement.name }} · {{ (replacement.size / 1024).toFixed(0) }} kB</p>
                  <img :src="replacementUrl" alt="Náhled náhradního obrázku" @error="invalidPreview" />
                  <div class="character-admin__actions"><AwButton variant="primary" :busy="saving" :disabled="dirty" @click="write('portrait')">Nahrát náhradní obrázek</AwButton><AwButton :disabled="saving" @click="clearReplacement">Zrušit výběr</AwButton></div>
                  <p v-if="dirty">Nejprve uložte údaje postavy nebo načtěte aktuální verzi.</p>
                </template>
              </fieldset>
            </section>
          </template>
        </section>
      </div>
    </template>
  </section>
</template>

<style scoped>
.character-admin { max-width: 78rem; margin-inline: auto; }
.character-admin > header { max-width: 68ch; }
.character-admin__toolbar { display: flex; flex-wrap: wrap; align-items: end; gap: 1rem; }
.character-admin__toolbar .aw-field { flex: 1; min-width: min(100%, 16rem); }
.character-admin__layout { display: grid; grid-template-columns: minmax(12rem, .85fr) minmax(0, 2fr); gap: 1.5rem; align-items: start; }
.character-admin__list,
.character-admin__editor { min-width: 0; }
.character-admin__list h2 small { color: var(--aw-muted); font-size: .85rem; font-weight: 600; }
.character-admin__list ul { list-style: none; padding: 0; margin: 0; display: grid; gap: .5rem; }
.character-admin__list button { display: grid; width: 100%; min-height: 64px; text-align: left; gap: .3rem; padding: .85rem; color: inherit; font: inherit; border: 1px solid var(--aw-border-subtle); border-radius: var(--wns-radius-field); background: transparent; cursor: pointer; overflow-wrap: anywhere; }
.character-admin__list button[aria-pressed='true'] { border-color: var(--aw-accent); background: var(--aw-accent-soft); }
.character-admin__list button:focus-visible { outline: 2px solid var(--aw-accent); outline-offset: 3px; }
.character-admin__list small, .character-admin__meta { color: var(--aw-muted); font-size: .85rem; }
.character-admin__editor { min-width: 0; overflow-wrap: anywhere; }
.character-admin__editor fieldset { border: 0; padding: 0; margin: 1.5rem 0 0; min-width: 0; display: grid; gap: 1rem; }
.character-admin__editor legend { margin-bottom: 1rem; font-weight: 600; }
.character-admin__editor .aw-button { justify-self: start; }
.character-admin__portrait { margin-top: 2rem; padding-top: 1rem; border-top: 1px solid var(--aw-border-subtle); }
.character-admin__portrait img { display: block; max-width: min(100%, 20rem); max-height: 25rem; object-fit: contain; border-radius: .5rem; }
.character-admin__portrait input { max-width: 100%; }
.character-admin__actions { display: flex; flex-wrap: wrap; gap: .5rem; }
.character-admin__empty { padding-block: 1rem; }
.character-admin__empty p { margin-top: 0; }
.character-admin__casting { display: grid; justify-items: center; text-align: center; padding: 2rem 1rem; }
.character-admin__casting > img { filter: invert(1); opacity: .65; margin-bottom: 1rem; }
.character-admin__aside { color: var(--wns-violet); max-width: 32ch; font-size: .9rem; }
@media (max-width: 720px) { .character-admin__layout { grid-template-columns: minmax(0, 1fr); } .character-admin__list ul { max-height: 20rem; overflow-y: auto; overscroll-behavior: contain; } }
</style>
