<script setup lang="ts">
import LaunchManagementPanel from './LaunchManagementPanel.vue';
</script>

<template>
  <div class="page-stack launch-management-page">
    <RouterLink to="/settings/admin">← Zpět ke správě účtů a oprávnění</RouterLink>
    <header>
      <h1>Večer a pozvánky</h1>
      <p>Připravte skupinu, pozvěte účastníky a potvrďte odpočet do otevření večera.</p>
    </header>
    <LaunchManagementPanel />
  </div>
</template>

<style scoped>
.launch-management-page { max-width: 74rem; margin: 0 auto; }
</style>
