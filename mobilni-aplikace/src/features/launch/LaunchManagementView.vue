<script setup lang="ts">
import LaunchManagementPanel from './LaunchManagementPanel.vue';
</script>

<template>
  <main class="page-stack launch-management-page">
    <RouterLink to="/settings/admin">← Zpět ke správě účtů a oprávnění</RouterLink>
    <LaunchManagementPanel />
  </main>
</template>

<style scoped>
.launch-management-page { max-width: 74rem; margin: 0 auto; }
</style>
