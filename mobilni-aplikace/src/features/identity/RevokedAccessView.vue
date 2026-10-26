<script setup lang="ts">
import AwButton from '../../components/AwButton.vue';
import { account, signOut } from './session';
import { computed } from 'vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

const english = computed(() => interfaceLanguagePreference.language.value === 'en');
</script>

<template>
  <section class="revoked-access" aria-labelledby="revoked-title">
    <p class="eyebrow">Wand Night Stand</p>
    <h1 id="revoked-title">{{ english ? 'Your beta access has been removed.' : 'Tvůj přístup k testování byl odebrán.' }}</h1>
    <p>{{ english ? 'Your account and saved data have not been deleted.' : 'Účet ani uložená data se tím nesmazaly.' }}</p>
    <p v-if="account" class="account-name">{{ account.displayName }}</p>
    <nav :aria-label="english ? 'Account help' : 'Nápověda k účtu'">
      <RouterLink to="/me/account">{{ english ? 'Account and saved data' : 'Účet a uložená data' }}</RouterLink>
      <RouterLink to="/privacy">{{ english ? 'Privacy' : 'Soukromí' }}</RouterLink>
      <RouterLink to="/support">{{ english ? 'Support' : 'Podpora' }}</RouterLink>
    </nav>
    <AwButton @click="signOut">{{ english ? 'Sign out' : 'Odhlásit se' }}</AwButton>
  </section>
</template>

<style scoped>
.revoked-access { display: grid; gap: 1rem; width: 100%; max-width: 34rem; margin-inline: auto; padding: max(24px, env(safe-area-inset-top, 0px)) max(20px, env(safe-area-inset-right, 0px)) max(32px, env(safe-area-inset-bottom, 0px)) max(20px, env(safe-area-inset-left, 0px)); }
.revoked-access h1, .revoked-access p { margin: 0; }
.revoked-access nav { display: flex; flex-wrap: wrap; gap: .75rem 1rem; }
.revoked-access nav a { display: inline-flex; min-height: 44px; align-items: center; }
.account-name { color: var(--aw-muted); }
</style>
