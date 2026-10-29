<script setup lang="ts">
import { inject } from 'vue';
import { applicationHeaderActionsKey } from './applicationHeader';

const target = inject(applicationHeaderActionsKey, null);
</script>

<template>
  <Teleport :to="target ?? 'body'" :disabled="!target"><slot /></Teleport>
</template>
