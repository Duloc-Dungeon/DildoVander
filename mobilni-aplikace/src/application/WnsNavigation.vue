<script setup lang="ts">
import evening from '../assets/wns-v2/icons/regular/house.svg';
import character from '../assets/wns-v2/icons/regular/user.svg';
import keepsakes from '../assets/wns-v2/icons/regular/seal-check.svg';
import account from '../assets/wns-v2/icons/regular/key.svg';
const links = [
  { to: '/play', label: 'Večer', icon: evening },
  { to: '/me/character', label: 'Postava', icon: character },
  { to: '/me/keepsakes', label: 'Památky', icon: keepsakes },
  { to: '/me/account', label: 'Já', icon: account },
];
</script>

<template>
  <nav class="wns-game-navigation" aria-label="Hlavní navigace">
    <RouterLink v-for="link in links" :key="link.to" :to="link.to"><img :src="link.icon" alt="" width="23" height="23" /><span>{{ link.label }}</span></RouterLink>
  </nav>
</template>

<style scoped>
img { filter: invert(1); opacity: .7; }
a.router-link-active img { opacity: 1; }
</style>
