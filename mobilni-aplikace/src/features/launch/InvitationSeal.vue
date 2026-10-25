<template>
  <svg class="invitation-seal" viewBox="0 0 320 180" aria-hidden="true" focusable="false">
    <ellipse cx="160" cy="153" rx="100" ry="10" class="seal-shadow" />
    <path d="M71 61 238 44l12 103-168 18z" class="seal-paper" />
    <path d="m73 62 91 58 74-74M83 162l62-62m104 44-69-48" class="seal-fold" />
    <circle cx="164" cy="116" r="22" class="seal-wax" />
    <path d="m164 100 4 12 12 4-12 4-4 12-4-12-12-4 12-4z" class="seal-star" />
    <path d="M94 44c27-32 117-33 144-5M54 118c-29-26-9-73 20-86M265 66c31 27 14 72-12 90" class="seal-orbit" />
    <path d="m60 26 3 8 8 3-8 3-3 8-3-8-8-3 8-3zm195-6 2 6 6 2-6 2-2 6-2-6-6-2 6-2z" class="seal-star" />
    <circle cx="113" cy="20" r="2" class="seal-star" /><circle cx="274" cy="119" r="2" class="seal-star" />
  </svg>
</template>

<style scoped>
.invitation-seal { display: block; width: min(100%, 17rem); height: auto; margin: 0 auto -.25rem; }
.seal-paper { fill: var(--aw-surface); stroke: var(--aw-border); stroke-width: 1.2; }
.seal-fold { fill: none; stroke: var(--aw-border); stroke-width: 1; }
.seal-wax { fill: var(--aw-accent-soft); stroke: var(--aw-link); stroke-width: 1; }
.seal-star { fill: var(--aw-link); }
.seal-orbit { fill: none; stroke: var(--aw-link); stroke-opacity: .45; stroke-dasharray: 2 7; }
.seal-shadow { fill: var(--aw-accent); opacity: .06; }
</style>
