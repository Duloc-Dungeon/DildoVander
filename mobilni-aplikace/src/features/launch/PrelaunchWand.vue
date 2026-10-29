<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import WandWorkshop from '../game/WandWorkshop.vue';
import { loadWorkshop, type WorkshopEntry } from '../game/wandWorkshop';

const emit = defineEmits<{ 'open-change': [open: boolean] }>();
const entry = ref<WorkshopEntry>();
const opened = ref(false);
const saved = ref(false);
const loading = ref(true);
const error = ref(false);
const controller = new AbortController();
const summary = ref<HTMLElement>();
const completed = computed(() => saved.value || (entry.value?.session?.status === 'complete' && !!entry.value.session.wand));
const resume = computed(() => !!entry.value?.session);

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
  <section class="prelaunch-wand" :data-open="opened" aria-labelledby="prelaunch-wand-title">
    <template v-if="!opened">
      <h2 id="prelaunch-wand-title" ref="summary" tabindex="-1">{{ completed ? 'Postava i hůlka jsou připravené.' : 'Postavu už znáš. Teď tvoje hůlka.' }}</h2>
      <p v-if="completed" role="status">Hůlka je uložená. Ollivander Industries™ děkuje za důvěru. Považuje ji za odvážnou.</p>
      <p v-else>Než se večer otevře, promluv si s kufrem a vytvoř hůlku pro svoji postavu.</p>
      <p class="return-reassurance">Postava i uložený postup zůstávají pod tvým účtem. Můžeš odejít a vrátit se později.</p>
      <p v-if="loading" role="status">Ověřujeme tvoji dílnu…</p>
      <AwNotice v-else-if="error" tone="warning">Dílnu se nepodařilo načíst. <AwButton @click="refresh">Zkusit znovu</AwButton></AwNotice>
      <AwButton v-else variant="primary" @click="open">{{ completed ? 'Prohlédnout hůlku' : resume ? 'Pokračovat v tvorbě hůlky' : 'Vytvořit hůlku' }}</AwButton>
    </template>
    <template v-else>
      <h2 id="prelaunch-wand-title" class="sr-only">Tvoje hůlka před začátkem večera</h2>
      <WandWorkshop prelaunch @saved="saved = true" @exit="close" />
    </template>
  </section>
</template>

<style scoped>
.prelaunch-wand { width: min(100%, 62rem); min-width: 0; display: grid; gap: var(--aw-space-3); margin: var(--aw-space-4) auto; }
.prelaunch-wand[data-open='false'] { width: min(100%, 42rem); }
.prelaunch-wand h2, .prelaunch-wand p { margin: 0; }
.prelaunch-wand .return-reassurance { color: var(--wns-muted); font-size: .9rem; line-height: 1.5; }
.prelaunch-wand > .aw-button { justify-self: start; }
</style>
