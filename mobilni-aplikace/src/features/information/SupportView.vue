<script setup lang="ts">
import { customerCopy } from '../../application/customerCopy';
import { launchAllowed } from '../../application/launchAccess';
import InformationBackLink from './InformationBackLink.vue';
</script>

<template>
  <article class="page-stack information-page" aria-labelledby="support-title">
    <InformationBackLink />
    <template v-if="launchAllowed !== true">
      <p class="eyebrow">Wand Night Stand</p>
      <h1 id="support-title">Pomoc s pozvánkou a účtem</h1>
      <section>
        <h2>Pozvánka a přihlášení</h2>
        <p>Otevřete osobní pozvánku na stejném zařízení a dokončete přihlášení přístupovým klíčem. Pokud je v zařízení přihlášen jiný účet, odhlaste jej a pokračujte s pozvánkou.</p>
      </section>
      <section>
        <h2>Příprava portrétu</h2>
        <p>Příprava může pokračovat na pozadí. Pokud se nezdaří, stránka postavy nabídne opakování.</p>
      </section>
      <section>
        <h2>Účet a soukromí</h2>
        <p>Odstranění účtu můžete zahájit a ověřit na stránce <RouterLink to="/account-deletion">odstranění účtu</RouterLink>. Podrobnosti o údajích a jejich zpracování jsou v <RouterLink to="/privacy">informacích o soukromí</RouterLink>.</p>
      </section>
      <section>
        <h2>Kontaktujte nás</h2>
        <p>Napište na <a href="mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20support">support@dildovander.de</a>. Do zprávy neposílejte přístupové klíče.</p>
      </section>
    </template>
    <template v-else>
    <p class="eyebrow">Wand Night Stand</p>
    <h1 id="support-title">{{ customerCopy.text('support.title') }}</h1>
    <p>{{ customerCopy.text('support.intro') }}</p>

    <section aria-labelledby="support-contact">
      <h2 id="support-contact">{{ customerCopy.text('support.contact-title') }}</h2>
      <p>{{ customerCopy.text('support.contact') }} <a href="mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20support">support@dildovander.de</a>.</p>
      <p>{{ customerCopy.text('support.contact-guidance') }}</p>
    </section>

    <section aria-labelledby="support-connection">
      <h2 id="support-connection">{{ customerCopy.text('support.connection-title') }}</h2>
      <p>{{ customerCopy.text('support.connection') }}</p>
      <p>{{ customerCopy.text('support.safety') }}</p>
    </section>

    <section aria-labelledby="support-account">
      <h2 id="support-account">{{ customerCopy.text('support.account-title') }}</h2>
      <p>{{ customerCopy.text('support.account') }}</p>
    </section>

    <section aria-labelledby="support-data">
      <h2 id="support-data">{{ customerCopy.text('support.data-title') }}</h2>
      <p>{{ customerCopy.text('support.deletion') }}</p>
      <p>{{ customerCopy.text('support.deletion-status') }} <RouterLink to="/account-deletion">{{ customerCopy.text('support.deletion-status-link') }}</RouterLink> {{ customerCopy.text('support.deletion-followup') }}</p>
      <p>{{ customerCopy.text('support.data-request') }}</p>
      <p><RouterLink to="/privacy">{{ customerCopy.text('support.privacy-link') }}</RouterLink></p>
    </section>
    </template>
  </article>
</template>
