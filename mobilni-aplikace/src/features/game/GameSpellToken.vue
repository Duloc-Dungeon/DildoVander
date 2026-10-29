<script setup lang="ts">
import strikeToken from '../../assets/brand/responsive-table/token-strike.webp';
import trickToken from '../../assets/brand/responsive-table/token-trick.webp';
import shieldToken from '../../assets/brand/responsive-table/token-shield.webp';
import type { Choice } from './game';

defineProps<{ action: Choice }>();
const tokens = { strike: strikeToken, trick: trickToken, shield: shieldToken };
</script>

<template>
  <img class="game-spell-token" :src="tokens[action]" alt="" width="110" height="110" />
</template>
