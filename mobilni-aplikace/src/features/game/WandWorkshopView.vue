<script setup lang="ts">
import { useRoute } from 'vue-router';
import WandWorkshop from './WandWorkshop.vue';
const route = useRoute();
</script>
<template><WandWorkshop :upgrade="route.query.upgrade === '1'" /></template>
