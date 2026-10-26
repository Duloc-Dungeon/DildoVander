<script setup lang="ts">
import { ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { authenticatePasskey } from './session';

const emit = defineEmits<{ verified: [] }>();
const busy = ref(false);
const error = ref('');

async function verify(): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  error.value = '';
  try {
    await authenticatePasskey('reauth');
    emit('verified');
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Totožnost se nepodařilo ověřit. Zkuste to znovu.';
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="admin-reauthentication">
    <p>Správa vyžaduje čerstvé ověření. Po ověření původní změnu odešlete znovu.</p>
    <AwButton :busy="busy" @click="verify">Ověřit totožnost přístupovým klíčem</AwButton>
    <p v-if="error" role="alert">{{ error }}</p>
  </div>
</template>
