<script setup lang="ts">
import info from '../assets/wns-v2/icons/regular/info.svg';
import warning from '../assets/wns-v2/icons/regular/warning.svg';
import sealCheck from '../assets/wns-v2/icons/regular/seal-check.svg';

const icons = { info, warning, danger: warning, success: sealCheck };

withDefaults(defineProps<{
  tone?: 'info' | 'warning' | 'danger' | 'success';
  title?: string;
  live?: boolean;
}>(), { tone: 'info', live: true });
</script>

<template>
  <div :class="['aw-notice', 'aw-notice--with-icon', `aw-notice--${tone}`]"
    :role="live ? (tone === 'danger' ? 'alert' : 'status') : undefined">
    <span class="aw-notice__icon" aria-hidden="true"><img :src="icons[tone]" alt="" width="20" height="20" /></span>
    <div class="aw-notice__content"><strong v-if="title">{{ title }}</strong><div><slot /></div></div>
  </div>
</template>
