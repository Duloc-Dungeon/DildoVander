<script setup lang="ts">
import curtain from '../../assets/brand/invitation-curtain-small.webp';
</script>

<template><div class="invitation-seal" aria-hidden="true"><img :src="curtain" alt="" width="480" height="640" /></div></template>

<style scoped>
.invitation-seal { width: 100%; height: 11rem; overflow: hidden; border-radius: 12px; margin-bottom: 1rem; }
.invitation-seal img { display: block; width: 100%; height: 100%; object-fit: cover; object-position: 50% 65%; }
</style>
