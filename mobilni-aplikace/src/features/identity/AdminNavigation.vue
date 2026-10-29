<script setup lang="ts">
import usersIcon from '../../assets/wns-v2/icons/regular/users.svg';
import eveningIcon from '../../assets/wns-v2/icons/regular/moon-stars.svg';
import characterIcon from '../../assets/wns-v2/icons/regular/identification-card.svg';

defineProps<{ current: 'accounts' | 'evening' | 'characters' }>();
const links = [
  { id: 'accounts', to: '/settings/admin', label: 'Účty', icon: usersIcon },
  { id: 'evening', to: '/settings/admin/launch', label: 'Večer', icon: eveningIcon },
  { id: 'characters', to: '/settings/admin/characters', label: 'Postavy', icon: characterIcon },
];
</script>

<template>
  <nav class="admin-navigation" aria-label="Správa aplikace">
    <strong class="admin-navigation__title">Zákulisí večera</strong>
    <div class="admin-navigation__links">
      <RouterLink v-for="link in links" :key="link.id" :to="link.to"
        :aria-current="current === link.id ? 'page' : undefined">
        <img :src="link.icon" alt="" width="22" height="22" />
        <span>{{ link.label }}</span>
      </RouterLink>
    </div>
  </nav>
</template>

<style scoped>
.admin-navigation { display: flex; flex-wrap: wrap; align-items: center; gap: 1rem 2rem; padding-block: 1rem; border-block: 1px solid var(--aw-border-subtle); }
.admin-navigation__title { color: var(--aw-muted); font-size: .9rem; font-weight: 600; }
.admin-navigation__links { display: flex; flex: 1; gap: .5rem; }
.admin-navigation a { display: flex; align-items: center; justify-content: center; gap: .65rem; min-height: 48px; padding: .65rem 1rem; border-radius: var(--wns-radius-field); color: var(--aw-muted); text-decoration: none; font-weight: 700; }
.admin-navigation img { filter: invert(1); opacity: .75; }
.admin-navigation a:hover { background: var(--aw-elevated); color: var(--aw-ivory); }
.admin-navigation a[aria-current='page'] { background: var(--wns-mint); color: var(--wns-on-mint); }
.admin-navigation a[aria-current='page'] img { filter: none; opacity: 1; }
.admin-navigation a:focus-visible { outline: 2px solid var(--aw-accent); outline-offset: 3px; }
@media (max-width: 560px) {
  .admin-navigation { gap: .65rem; }
  .admin-navigation__title { width: 100%; }
  .admin-navigation__links { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .admin-navigation a { flex-direction: column; gap: .4rem; padding: .65rem .4rem; }
}
</style>
