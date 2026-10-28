<script setup lang="ts">
import { computed } from 'vue';
import { useRouter } from 'vue-router';
import { account } from '../identity/session';
import { isCustomerAccount } from '../../application/access';
import { informationReturnPath } from '../../application/informationNavigation';

const router = useRouter();
const saved = informationReturnPath(router.options.history.state.informationReturn);
const destination = computed(() => saved || (account.value ? isCustomerAccount(account.value) ? '/me/account' : '/settings' : '/signin'));
</script>

<template>
  <RouterLink class="information-back" :to="destination">← Zpět</RouterLink>
</template>

<style scoped>
.information-back { display: inline-flex; align-items: center; min-height: 44px; justify-self: start; }
</style>
