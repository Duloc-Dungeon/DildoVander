<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { account } from '../identity/session';
import { customerMembershipState, launchPolicySnapshot } from '../../application/launchAccess';
import RickrollVideo from '../media/RickrollVideo.vue';
import { readRickrollVideo } from '../media/rickrollMedia';
import { markRickrollSeen, rickrollSeen, withinRickrollWindow } from './rickrollWindow';

const route = useRoute();
const source = ref<string>();
const active = ref(false);
const dialog = ref<HTMLDialogElement>();
let generation = 0;
let launchAt: number | undefined;
let owner: string | undefined;
let controller: AbortController | undefined;
const completed = new Set<string>();
watch(active, async visible => {
  await nextTick();
  if (visible) dialog.value?.showModal();
  else dialog.value?.close();
});

watch([() => account.value?.id, () => route.path, launchPolicySnapshot, customerMembershipState], async () => {
  const token = ++generation;
  controller?.abort();
  if (owner !== account.value?.id || route.path === '/launch' || customerMembershipState.value !== 'active') active.value = false;
  const policy = launchPolicySnapshot.value;
  const id = account.value?.id;
  const openedAt = policy?.openedAt ?? policy?.launchAt ?? null;
  if (active.value || !id || route.path === '/launch' || !policy?.allowed
    || customerMembershipState.value !== 'active' || document.hidden
    || !withinRickrollWindow(openedAt, policy.serverNow + Math.max(0, performance.now() - (policy.observedAt ?? performance.now()))) || openedAt === null
    || rickrollSeen(id, openedAt) || completed.has(`${id}:${openedAt}`)) return;
  controller = new AbortController();
  const media = await readRickrollVideo(controller.signal);
  if (token !== generation || !media) return;
  owner = id;
  launchAt = openedAt;
  source.value = media;
  active.value = true;
}, { immediate: true });

function dismiss(): void {
  if (owner && launchAt !== undefined) {
    markRickrollSeen(owner, launchAt);
    completed.add(`${owner}:${launchAt}`);
  }
  active.value = false;
}
onBeforeUnmount(() => { generation++; controller?.abort(); });
</script>

<template>
  <dialog ref="dialog" class="group-rickroll" aria-label="Hudební přivítání" @cancel.prevent>
    <RickrollVideo v-if="source" :src="source" :active="active" @dismiss="dismiss" />
  </dialog>
</template>

<style scoped>
.group-rickroll { position: fixed; inset: 0; margin: 0; width: 100%; height: 100dvh; max-width: none; max-height: none; padding: 0; border: 0; z-index: 10000; overflow: auto; background: #000; }
.group-rickroll:not([open]) { display: none; }
</style>
