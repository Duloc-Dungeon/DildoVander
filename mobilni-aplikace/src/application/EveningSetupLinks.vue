<script setup lang="ts">
import { computed, useId } from 'vue';
import { account } from '../features/identity/session';
const titleId = `evening-setup-${useId()}`;
const canManage = computed(() => account.value?.roles.some(role => ['Owner', 'Administrator'].includes(role)) === true);
const canInvite = computed(() => account.value?.roles.includes('Owner') === true);
</script>

<template>
  <section v-if="canManage" class="evening-setup" :aria-labelledby="titleId">
    <header><p class="eyebrow">Pro hostitele</p><h2 :id="titleId">Tvůj večer začíná tady.</h2><p>{{ canInvite ? 'Nastav skupinu, pozvi hosty a připrav společný začátek.' : 'Nastav skupinu a připrav společný začátek.' }}</p></header>
    <nav class="evening-setup-steps" aria-label="Příprava večera">
      <RouterLink to="/settings/admin/launch#waiting-room"><span class="evening-step-number" aria-hidden="true">01</span><strong>Připravit večer</strong><span>Skupina, odpočet a otevření</span><span class="evening-step-arrow" aria-hidden="true">↗</span></RouterLink>
      <RouterLink v-if="canInvite" to="/settings/admin#invitations"><span class="evening-step-number" aria-hidden="true">02</span><strong>Pozvat hosty</strong><span>Pozvánky a přístupy</span><span class="evening-step-arrow" aria-hidden="true">↗</span></RouterLink>
      <RouterLink to="/play"><span class="evening-step-number" aria-hidden="true">{{ canInvite ? '03' : '02' }}</span><strong>Vyzkoušet hru</strong><span>Postavy a příběhy</span><span class="evening-step-arrow" aria-hidden="true">↗</span></RouterLink>
    </nav>
  </section>
</template>

<style scoped>
.evening-setup { grid-column: 1 / -1; min-width: 0; overflow-wrap: anywhere; padding: clamp(1.25rem, 3vw, 2rem); border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); background: var(--aw-surface); }
.evening-setup header p { margin: 0; color: var(--aw-muted); }
.evening-setup header .eyebrow { color: var(--aw-link); }
.evening-setup h2 { margin: .5rem 0; font-family: var(--aw-font-display); font-size: clamp(1.65rem, 3vw, 2.2rem); font-weight: 500; }
.evening-setup-steps { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); margin-top: 1.5rem; gap: .75rem; }
.evening-setup-steps a { position: relative; display: grid; grid-template-columns: minmax(0, 1fr); min-width: 0; gap: .4rem; padding: 1rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-control); color: var(--aw-ivory); text-decoration: none; background: var(--aw-void); }
.evening-setup-steps a > span:not(.evening-step-arrow) { color: var(--aw-muted); font-size: .8rem; }
.evening-setup-steps .evening-step-number { font-family: var(--aw-font-mono); margin-bottom: .7rem; }
.evening-step-arrow { position: absolute; top: 1rem; right: 1rem; color: var(--aw-link); }
.evening-setup-steps a:hover { border-color: var(--aw-accent); }
@media (max-width: 600px) { .evening-setup { padding: 1rem; } .evening-setup-steps { grid-template-columns: minmax(0, 1fr); } .evening-setup-steps a { padding: .75rem; } .evening-setup-steps .evening-step-number { margin-bottom: .25rem; } }
</style>
