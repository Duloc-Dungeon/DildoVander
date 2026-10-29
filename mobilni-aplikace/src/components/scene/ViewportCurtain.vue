<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useAnimate } from 'motion-v';

const props = withDefaults(defineProps<{ durationMs: number; active?: boolean }>(), { active: true });
const emit = defineEmits<{ finished: [] }>();
const [scope, animate] = useAnimate<HTMLElement>();
const opening = ref(false);
const finished = ref(false);
let frame: number | undefined;
let completion: ReturnType<typeof setTimeout> | undefined;
let controls: ReturnType<typeof animate> | undefined;
let generation = 0;

function clearRun(): void {
  generation++;
  if (frame !== undefined) cancelAnimationFrame(frame);
  frame = undefined;
  if (completion !== undefined) clearTimeout(completion);
  completion = undefined;
  controls?.stop();
  controls = undefined;
}

function finish(expected: number): void {
  if (finished.value || expected !== generation) return;
  finished.value = true;
  clearRun();
  emit('finished');
}

function begin(): void {
  if (finished.value) return;
  const expected = ++generation;
  if (!props.active) { finish(expected); return; }
  frame = requestAnimationFrame(() => {
    frame = requestAnimationFrame(() => {
      frame = undefined;
      if (finished.value || expected !== generation) return;
      if (!props.active) {
        finish(expected);
        return;
      }

      opening.value = true;
      const duration = props.durationMs / 1_000;
      completion = setTimeout(() => finish(expected), props.durationMs + 120);
      try {
        controls = animate([
          ['.viewport-curtain__wing--left', { x: ['0%', '-105%'] }, { duration, ease: [0.22, 1, 0.36, 1], at: 0 }],
          ['.viewport-curtain__wing--right', { x: ['0%', '105%'] }, { duration, ease: [0.22, 1, 0.36, 1], at: 0 }]
        ]);
        controls.then(() => finish(expected), () => finish(expected));
      } catch {
        finish(expected);
      }
    });
  });
}

watch(() => props.active, active => {
  if (!active && !finished.value) finish(generation);
});

onMounted(begin);
onBeforeUnmount(clearRun);
</script>

<template>
  <Teleport to="body">
    <div v-if="!finished" ref="scope" class="viewport-curtain" :class="{ 'viewport-curtain--opening': opening }" aria-hidden="true">
      <div class="viewport-curtain__wing viewport-curtain__wing--left"><span class="viewport-curtain__seam" /></div>
      <div class="viewport-curtain__wing viewport-curtain__wing--right"><span class="viewport-curtain__seam" /></div>
    </div>
  </Teleport>
</template>

<style scoped>
.viewport-curtain { position: fixed; z-index: 1000; inset: 0; overflow: clip; pointer-events: none; transform: none; animation: none; }
.viewport-curtain__wing { position: absolute; top: -2px; bottom: -2px; width: calc(50% + 4px); overflow: hidden; background-color: #292432; background-image: linear-gradient(180deg, rgb(8 9 9 / 42%), transparent 13%, transparent 82%, rgb(8 9 9 / 28%)), linear-gradient(90deg, #1c1823 0%, #302839 12%, #211c28 25%, #382e40 38%, #241f2b 51%, #342a3b 65%, #1d1924 79%, #302637 100%); }
.viewport-curtain__wing--left { left: -2px; }
.viewport-curtain__wing--right { right: -2px; }
.viewport-curtain__seam { position: absolute; top: 0; bottom: 0; width: 1px; background: linear-gradient(180deg, transparent 3%, rgb(255 198 98 / 16%) 18%, rgb(255 198 98 / 55%) 50%, rgb(255 198 98 / 16%) 82%, transparent 97%); }
.viewport-curtain__wing--left .viewport-curtain__seam { right: 0; }
.viewport-curtain__wing--right .viewport-curtain__seam { left: 0; }
</style>
