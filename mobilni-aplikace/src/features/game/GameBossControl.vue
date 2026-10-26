<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest, authenticatePasskey } from '../identity/session';
import { operation, type BossProfile } from './game';
const props = defineProps<{ groupId: string }>();
interface Preview { profile: BossProfile; proposedReferenceXp: number | null; activePlayers: number; minXp: number | null; maxXp: number | null }
const preview = ref<Preview>(); const busy = ref(false); const error = ref('');
let generation = 0;
async function read() {
  const token = ++generation;
  try {
    const value = await accountRequest<Preview>(`/app/v1/admin/game/groups/${encodeURIComponent(props.groupId)}/boss`);
    if (token === generation) { preview.value = value; error.value = ''; }
  } catch { if (token === generation) error.value = 'Herní výzvu se nepodařilo načíst.'; }
}
async function reveal() {
  if (busy.value || !preview.value?.proposedReferenceXp) return;
  busy.value = true;
  try {
    await accountRequest(`/app/v1/admin/game/groups/${encodeURIComponent(props.groupId)}/boss`, 'POST', {
      operationId: operation(), expectedRevision: preview.value.profile.revision, expectedReferenceXp: preview.value.proposedReferenceXp,
    });
    await read();
  } catch { error.value = 'Odhalení nebylo potvrzeno. Obnovte náhled; pro změnu potřebujete čerstvé ověření přístupovým klíčem.'; }
  finally { busy.value = false; }
}
async function reauthenticate() { try { await authenticatePasskey('reauth'); error.value = ''; } catch { error.value = 'Ověření nebylo dokončeno.'; } }
watch(() => props.groupId, () => { preview.value = undefined; void read(); }, { immediate: true });
onBeforeUnmount(() => { generation++; });
</script>
<template>
  <section class="game-boss-control" aria-label="Společný herní boss">
    <h4>Společný herní boss</h4>
    <AwNotice v-if="error" tone="warning">{{ error }}</AwNotice>
    <template v-if="preview?.profile">
      <p v-if="preview.profile.revealed" role="status">Baron je odhalený. Referenční síla je pevně {{ preview.profile.referenceXp }} XP. Ollivander {{ preview.profile.rescued ? 'je volný' : 'je v zajetí' }}.</p>
      <template v-else><p>Aktivních hráčů: {{ preview.activePlayers }}. Jejich XP: {{ preview.minXp ?? '—' }} až {{ preview.maxXp ?? '—' }}. Navržený medián: {{ preview.proposedReferenceXp ?? '—' }} XP.</p><p>Odhalení uzamkne obtížnost pro skupinu a přepne digitální dílnu do zajetí. První potvrzená výhra osvobodí Ollivandera všem. Fyzická cena není součástí této výzvy.</p><AwButton :busy="busy" :disabled="!preview.proposedReferenceXp" @click="reveal">Odhalit bosse a uzamknout sílu</AwButton></template>
    </template>
    <AwButton :disabled="busy" @click="read">Obnovit náhled</AwButton><AwButton v-if="error" :disabled="busy" @click="reauthenticate">Ověřit přístupovým klíčem</AwButton>
  </section>
</template>
<style scoped>.game-boss-control { display: grid; gap: .75rem; padding-block: 1rem; border-top: 1px solid var(--aw-border); }</style>
