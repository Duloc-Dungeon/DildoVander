<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import evening from '../assets/wns-v2/icons/regular/house.svg';
import character from '../assets/wns-v2/icons/regular/user.svg';
import keepsakes from '../assets/wns-v2/icons/regular/seal-check.svg';
import account from '../assets/wns-v2/icons/regular/key.svg';
const emit = defineEmits<{ resize: [height: number] }>();
const route = useRoute();
const navigation = ref<HTMLElement>();
let observer: ResizeObserver | undefined;
onMounted(() => {
  const measure = () => emit('resize', navigation.value?.getBoundingClientRect().height ?? 0);
  measure();
  if (typeof ResizeObserver !== 'undefined' && navigation.value) {
    observer = new ResizeObserver(measure);
    observer.observe(navigation.value);
  }
});
onBeforeUnmount(() => observer?.disconnect());
const links = [
  { to: '/play', label: 'Večer', icon: evening },
  { to: '/me/character', alias: '/me/conversation', label: 'Postava', icon: character },
  { to: '/me/keepsakes', label: 'Památky', icon: keepsakes },
  { to: '/me/account', label: 'Já', icon: account },
];
function isSelected(link: { to: string; alias?: string }): boolean {
  return route.path === link.to || route.path.startsWith(`${link.to}/`) || route.path === link.alias;
}
</script>

<template>
  <nav ref="navigation" class="wns-game-navigation" aria-label="Hlavní navigace">
    <RouterLink v-for="link in links" :key="link.to" v-slot="{ href, navigate }" :to="link.to" custom>
      <a :href="href" :class="{ 'router-link-active': isSelected(link) }" :aria-current="isSelected(link) ? 'page' : undefined" @click="navigate"><img :src="link.icon" alt="" width="23" height="23" /><span>{{ link.label }}</span></a>
    </RouterLink>
  </nav>
</template>

<style scoped>
img { filter: invert(1); opacity: .7; }
a.router-link-active img { opacity: 1; }
</style>
