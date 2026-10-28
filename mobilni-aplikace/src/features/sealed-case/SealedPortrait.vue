<script setup lang="ts">
import { onScopeDispose, ref, watch } from 'vue';
import { account, accountTransport } from '../identity/session';
const props = defineProps<{ src: string; viewer: string; name: string }>();
const image = ref(''); let epoch = 0; let controller: AbortController | undefined;
function clear() { epoch++; controller?.abort(); if (image.value) URL.revokeObjectURL(image.value); image.value = ''; }
watch(() => [props.src, props.viewer, account.value?.id], async () => {
  clear(); if (!props.src || account.value?.id !== props.viewer) return;
  const captured = epoch; const request = controller = new AbortController(); let candidate = '';
  try {
    const response = await accountTransport(props.src, { signal: request.signal });
    if (!response.ok) return;
    const blob = await response.blob();
    if (!['image/png', 'image/jpeg', 'image/webp'].includes(blob.type) || blob.size > 8 * 1024 * 1024 || !blob.size) return;
    candidate = URL.createObjectURL(blob);
    if (epoch === captured && !request.signal.aborted) { image.value = candidate; candidate = ''; }
  } catch { /* Missing private artwork does not block the saved character. */ }
  finally { if (candidate) URL.revokeObjectURL(candidate); }
}, { immediate: true });
onScopeDispose(clear);
</script>
<template><img v-if="image" :src="image" :alt="`Portrét: ${name}`" width="64" height="80" class="sealed-portrait" /></template>
<style scoped>.sealed-portrait { object-fit: cover; border-radius: 10px; }</style>
