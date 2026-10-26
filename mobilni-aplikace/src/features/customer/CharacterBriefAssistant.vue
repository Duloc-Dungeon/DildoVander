<script setup lang="ts">
import { computed, ref, useId } from 'vue';
import AwButton from '../../components/AwButton.vue';

const props = withDefaults(defineProps<{ modelValue: string; disabled?: boolean }>(), { disabled: false });
const emit = defineEmits<{ 'update:modelValue': [value: string] }>();
const titleId = `character-brief-${useId()}`;
const announcement = ref('');
const ideas = [
  { title: 'Skryté tajemství', text: 'Navenek působí sebejistě, ale skrývá tajemství, které by mohlo změnit celý večer.' },
  { title: 'Nečekaná slabina', text: 'Má slabost pro uznání a občas slíbí víc, než dokáže splnit.' },
  { title: 'Velké přání', text: 'Chce dnes překonat vlastní strach a najít spojence, kterému může věřit.' },
] as const;
const remaining = computed(() => 1200 - props.modelValue.length);
function nextBrief(text: string): string {
  return [props.modelValue.trim(), text].filter(Boolean).join('\n');
}
function addIdea(text: string): void {
  if (props.disabled || props.modelValue.includes(text) || nextBrief(text).length > 1200) return;
  emit('update:modelValue', nextBrief(text));
  announcement.value = 'Nápad je v popisu. Uprav ho podle sebe a pak potvrď přípravu s AI.';
}
</script>

<template>
  <aside class="character-brief" :aria-labelledby="titleId">
    <div class="character-brief-heading"><span class="character-brief-mark" aria-hidden="true">✦</span><div><p class="eyebrow">Zadání pro AI</p><h3 :id="titleId">Dej postavě něco navíc.</h3></div></div>
    <p>Vyber inspiraci a uprav ji v popisu. Čím konkrétnější postava, tím osobnější podklady pro rozhovor.</p>
    <div class="character-brief-ideas">
      <AwButton v-for="idea in ideas" :key="idea.title"
        :disabled="disabled || modelValue.includes(idea.text) || nextBrief(idea.text).length > 1200"
        @click="addIdea(idea.text)">{{ modelValue.includes(idea.text) ? '✓ ' : '+ ' }}{{ idea.title }}</AwButton>
    </div>
    <small>Jsou to připravené náměty. AI se spustí až po tvém souhlasu a potvrzení přípravy. Zbývá {{ Math.max(0, remaining) }} znaků.</small>
    <p v-if="announcement" class="character-brief-feedback" role="status">{{ announcement }}</p>
  </aside>
</template>

<style scoped>
.character-brief { display: grid; min-width: 0; gap: .9rem; padding: clamp(1rem, 3vw, 1.5rem); border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); background: var(--aw-surface); overflow-wrap: anywhere; }
.character-brief > *, .character-brief-heading > div { min-width: 0; }
.character-brief-heading { display: flex; align-items: center; gap: .8rem; }
.character-brief-mark { display: grid; place-items: center; width: 2.6rem; height: 2.6rem; flex: 0 0 auto; border-radius: 50%; color: var(--aw-link); background: var(--aw-accent-soft); }
.character-brief h3, .character-brief p { margin: 0; }
.character-brief .eyebrow { color: var(--aw-link); margin-bottom: .25rem; }
.character-brief > p, .character-brief small { color: var(--aw-muted); }
.character-brief-ideas { display: flex; flex-wrap: wrap; gap: .5rem; }
.character-brief-ideas :deep(button) { white-space: normal; text-align: start; }
.character-brief .character-brief-feedback { color: var(--aw-ivory); }
</style>
