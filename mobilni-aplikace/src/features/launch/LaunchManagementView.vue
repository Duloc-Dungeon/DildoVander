<script setup lang="ts">
import LaunchManagementPanel from './LaunchManagementPanel.vue';
import AdminNavigation from '../identity/AdminNavigation.vue';
</script>

<template>
  <div class="page-stack launch-management-page">
    <header>
      <h1>Večer a pozvánky</h1>
      <p>Připravte skupinu, pozvěte účastníky a nastavte spuštění aplikace pro nové hosty.</p>
    </header>
    <AdminNavigation current="evening" />
    <LaunchManagementPanel />
  </div>
</template>

<style scoped>
.launch-management-page { max-width: 74rem; margin: 0 auto; }
</style>
