<script setup lang="ts">
import { ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import AccountDeletion from './AccountDeletion.vue';
import { account, accountRequest, refreshIdentity, signOut } from './session';
import { aiConsentVersion } from './aiConsent';
const accepted = ref(false);
const busy = ref(false);
const error = ref(false);
watch(() => account.value?.id, () => { accepted.value = false; busy.value = false; error.value = false; }, { flush: 'sync' });
async function confirm(): Promise<void> {
  if (!accepted.value || busy.value) return;
  const accountId = account.value?.id;
  busy.value = true; error.value = false;
  try {
    await accountRequest('/app/v1/account/ai-consent', 'POST', { openAiConsent: true, consentVersion: aiConsentVersion });
    if (account.value?.id === accountId) await refreshIdentity();
  } catch { if (account.value?.id === accountId) error.value = true; }
  finally { if (account.value?.id === accountId) busy.value = false; }
}
</script>
<template>
  <section class="account-ai-consent" aria-labelledby="account-ai-consent-title">
    <h1 id="account-ai-consent-title">Dokonči nastavení účtu</h1>
    <p>Postavu, portrét, hůlku a hlasový rozhovor připravuje AI. Než budeš pokračovat, potřebujeme tvůj souhlas.</p>
    <AwNotice v-if="error" tone="danger" role="alert">Souhlas se nepodařilo uložit. Zkontroluj připojení a zkus to znovu.</AwNotice>
    <form @submit.prevent="confirm">
      <label><input v-model="accepted" type="checkbox" required :disabled="busy" /> Souhlasím se zpracováním zadání postavy, potřebného kontextu, přepisu a zvuku rozhovoru službami OpenAI a ElevenLabs pro přípravu osobního vtipu, portrétu, hůlky, rozpoznávání řeči a hlasových odpovědí.</label>
      <p><RouterLink to="/privacy">Informace o zpracování údajů</RouterLink></p>
      <AwButton type="submit" variant="primary" :disabled="!accepted" :busy="busy">Potvrdit a pokračovat</AwButton>
      <AwButton :disabled="busy" @click="signOut">Odhlásit se</AwButton>
    </form>
    <details><summary>Nechci pokračovat a chci odstranit účet</summary><AccountDeletion /></details>
  </section>
</template>
<style scoped>
.account-ai-consent { max-width: 38rem; margin-inline: auto; padding: 1.5rem; }
.account-ai-consent form { display: grid; gap: 1rem; }
.account-ai-consent label { display: flex; align-items: flex-start; gap: .75rem; min-height: 44px; }
.account-ai-consent input { flex: 0 0 20px; width: 20px; height: 20px; margin-top: .2rem; }
.account-ai-consent a { display: inline-flex; align-items: center; min-height: 44px; }
.account-ai-consent details { margin-top: 1.5rem; }
.account-ai-consent summary { min-height: 44px; padding-block: .6rem; cursor: pointer; }
</style>
