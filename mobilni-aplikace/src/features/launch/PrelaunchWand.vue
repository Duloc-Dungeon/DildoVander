<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import WandWorkshop from '../game/WandWorkshop.vue';
import { loadWorkshop, type WorkshopEntry } from '../game/wandWorkshop';
import { projectMobileGeneratedWand } from '../archive/generatedWand';

const emit = defineEmits<{ 'open-change': [open: boolean]; 'complete-change': [complete: boolean] }>();
const entry = ref<WorkshopEntry>();
const opened = ref(false);
const saved = ref(false);
const loading = ref(true);
const error = ref(false);
const controller = new AbortController();
const summary = ref<HTMLElement>();
const completed = computed(() => saved.value || (entry.value?.session?.status === 'complete' && !!entry.value.session.wand));
const resume = computed(() => !!entry.value?.session);
const wand = computed(() => projectMobileGeneratedWand(entry.value?.session?.wand));
watch(completed, value => emit('complete-change', value), { immediate: true });

async function refresh(): Promise<void> {
  loading.value = true;
  error.value = false;
  try {
    const value = await loadWorkshop(controller.signal);
    if (!controller.signal.aborted) entry.value = value;
  } catch {
    if (!controller.signal.aborted) error.value = true;
  } finally {
    if (!controller.signal.aborted) loading.value = false;
  }
}
async function open(): Promise<void> {
  opened.value = true;
  emit('open-change', true);
  await nextTick();
  document.getElementById('workshop-title')?.focus();
}
async function close(): Promise<void> {
  opened.value = false;
  emit('open-change', false);
  await refresh();
  await nextTick();
  summary.value?.focus();
}
onMounted(() => { void refresh(); });
onBeforeUnmount(() => { controller.abort(); });
</script>

<template>
  <section class="prelaunch-wand" :data-open="opened" :data-complete="completed" aria-labelledby="prelaunch-wand-title">
    <template v-if="!opened">
      <div class="prelaunch-wand-copy">
        <h2 id="prelaunch-wand-title" ref="summary" tabindex="-1">{{ completed ? 'Postava i hůlka jsou připravené.' : 'Postavu už znáš. Teď tvoje hůlka.' }}</h2>
        <p v-if="completed" role="status">Hůlka je uložená. Ollivander Industries™ děkuje za důvěru. Považuje ji za odvážnou.</p>
        <p v-else>V dílně na tebe čeká kufr. Promluv si s ním a vytvoř hůlku pro svoji postavu.</p>
        <p v-if="completed && wand" class="prelaunch-wand-keepsake">{{ wand.name }}<span>{{ wand.material }} · {{ wand.core }}</span></p>
      </div>
      <p v-if="loading" role="status">Ověřujeme tvoji dílnu…</p>
      <AwNotice v-else-if="error" tone="warning">Dílnu se nepodařilo načíst. <AwButton @click="refresh">Zkusit znovu</AwButton></AwNotice>
      <AwButton v-else variant="primary" @click="open">{{ completed ? 'Prohlédnout hůlku' : resume ? 'Pokračovat v tvorbě hůlky' : 'Vytvořit hůlku' }}</AwButton>
      <p class="return-reassurance">Postava i uložený postup zůstávají pod tvým účtem. Můžeš odejít a vrátit se později.</p>
    </template>
    <template v-else>
      <h2 id="prelaunch-wand-title" class="sr-only">Tvoje hůlka před začátkem večera</h2>
      <WandWorkshop prelaunch @saved="saved = true" @exit="close" />
    </template>
  </section>
</template>

<style scoped>
.prelaunch-wand { width: min(100%, 62rem); min-width: 0; display: grid; gap: var(--aw-space-3); margin: var(--aw-space-4) auto; }
.prelaunch-wand[data-open='true'] { margin-block: 0; }
.prelaunch-wand[data-open='false'] { width: min(100%, 62rem); padding: clamp(20px, 4vw, 32px); border-radius: 16px; background: var(--wns-mint); color: var(--wns-on-mint); }
.prelaunch-wand h2, .prelaunch-wand p { margin: 0; }
.prelaunch-wand h2 { max-width: 28ch; font-size: clamp(1.3rem, 4.5vw, 1.8rem); line-height: 1.2; letter-spacing: -.025em; text-wrap: balance; }
.prelaunch-wand-copy { display: grid; gap: 12px; min-width: 0; }
.prelaunch-wand-copy p { max-width: 62ch; line-height: 1.5; }
.prelaunch-wand .return-reassurance { grid-column: 1 / -1; font-size: .8rem; line-height: 1.5; }
.prelaunch-wand > :deep(.aw-button--primary) { justify-self: start; min-height: 52px; background: var(--wns-on-mint); color: var(--wns-mint); }
.prelaunch-wand > :deep(.aw-button--primary:hover) { background: var(--wns-surface-raised); }
.prelaunch-wand[data-open='false'] :deep(:is(a, button, input, select, textarea, summary):focus-visible) { outline: 3px solid var(--wns-on-mint); outline-offset: 4px; }
.prelaunch-wand-keepsake { overflow-wrap: anywhere; font-size: 1.15rem; font-weight: 750; }
.prelaunch-wand-keepsake span { display: block; font-size: .875rem; font-weight: 500; }
@media (min-width: 760px) { .prelaunch-wand[data-open='false'] { grid-template-columns: minmax(0, 1fr) auto; align-items: center; column-gap: 32px; } }
/* The launch page already supplies the embedded workshop's mobile gutters. */
@media (max-width: 849px) {
  .prelaunch-wand[data-open='true'] :deep(.trunk-workshop) { padding-inline: 0; }
}
</style>
