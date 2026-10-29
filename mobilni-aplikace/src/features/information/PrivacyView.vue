<script setup lang="ts">
import { customerCopy } from '../../application/customerCopy';
import { launchAllowed } from '../../application/launchAccess';
import InformationBackLink from './InformationBackLink.vue';
</script>

<template>
  <article class="page-stack information-page" aria-labelledby="privacy-title">
    <InformationBackLink />
    <template v-if="launchAllowed !== true">
      <p class="eyebrow">Wand Night Stand</p>
      <h1 id="privacy-title">Soukromí před spuštěním</h1>
      <p>Tato pozvánková fáze zahrnuje účet, přípravu fiktivní postavy a její portrét.</p>
      <section>
        <h2>Jaké údaje používáme</h2>
        <p>Přístup k účtu chrání přístupový klíč. Pro přípravu postavy uložíme její jméno a univerzum. Tyto údaje použijeme také k vytvoření portrétu a případně personalizované zprávy o načítání, pokud je tato možnost zapnutá.</p>
      </section>
      <section>
        <h2>Zpracování u OpenAI</h2>
        <p>Volba „Chci si nechat připravit osobní překvapení“ ve formuláři povoluje zpracování údajů o postavě. Teprve po vašem potvrzení odešleme její fiktivní jméno a univerzum poskytovatelům OpenAI pro obohacení profilu, osobní text a vytvoření portrétu. Přístupové klíče účtu se jim neposílají. Nezadávejte osobní ani citlivé údaje. Informace o zpracování najdete v <a href="https://openai.com/policies/privacy-policy/" target="_blank" rel="noopener noreferrer">zásadách ochrany soukromí OpenAI</a>.</p>
      </section>
      <section>
        <h2>Uložení a odstranění</h2>
        <p>Údaje účtu a postavy zůstávají v účtu, dokud nepožádáte o jeho odstranění. Zpracování portrétu může pokračovat na pozadí i po zavření aplikace. Odstranění účtu můžete zahájit v aplikaci; stav požadavku lze ověřit na stránce <RouterLink to="/account-deletion">odstranění účtu</RouterLink>.</p>
      </section>
      <section>
        <h2>Kontakt</h2>
        <p>Otázky k soukromí: <a href="mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy">support@dildovander.de</a>. Další pomoc najdete na stránce <RouterLink to="/support">podpory</RouterLink>.</p>
      </section>
    </template>
    <template v-else>
    <p class="eyebrow">Wand Night Stand</p>
    <h1 id="privacy-title">{{ customerCopy.text('privacy.title') }}</h1>
    <p>{{ customerCopy.text('privacy.updated') }}</p>

    <section aria-labelledby="privacy-contact">
      <h2 id="privacy-contact">{{ customerCopy.text('privacy.contact-title') }}</h2>
      <p>{{ customerCopy.text('privacy.contact') }} <a href="mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy">support@dildovander.de</a>.</p>
    </section>

    <section aria-labelledby="privacy-data">
      <h2 id="privacy-data">{{ customerCopy.text('privacy.data-title') }}</h2>
      <dl>
        <dt>{{ customerCopy.text('privacy.account-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.account') }}</dd>
        <dt>{{ customerCopy.text('privacy.device-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.device') }}</dd>
        <dt>{{ customerCopy.text('privacy.conversations-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.conversations') }}</dd>
        <dt>{{ customerCopy.text('privacy.character-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.character') }}</dd>
        <dt>{{ customerCopy.text('privacy.media-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.media') }}</dd>
        <dt>{{ customerCopy.text('privacy.technical-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.technical') }}</dd>
      </dl>
    </section>

    <section aria-labelledby="privacy-cloud">
      <h2 id="privacy-cloud">{{ customerCopy.text('privacy.cloud-title') }}</h2>
      <p>{{ customerCopy.text('privacy.cloud') }}</p>
      <p>{{ customerCopy.text('privacy.consent') }}</p>
      <p>{{ customerCopy.text('privacy.provider') }} <a href="https://openai.com/policies/privacy-policy/" target="_blank" rel="noopener noreferrer">{{ customerCopy.text('privacy.openai-link') }}</a>; {{ customerCopy.text('privacy.provider-limit') }}</p>
    </section>

    <section aria-labelledby="privacy-retention">
      <h2 id="privacy-retention">{{ customerCopy.text('privacy.retention-title') }}</h2>
      <p>{{ customerCopy.text('privacy.retention-intro') }}</p>
      <ul>
        <li>{{ customerCopy.text('privacy.retention-memory') }}</li>
        <li>{{ customerCopy.text('privacy.retention-keepsakes') }}</li>
        <li>{{ customerCopy.text('privacy.retention-media') }}</li>
        <li>{{ customerCopy.text('privacy.retention-logs') }}</li>
        <li>{{ customerCopy.text('privacy.retention-account') }}</li>
      </ul>
      <p>{{ customerCopy.text('privacy.retention-operator') }}</p>
    </section>

    <section aria-labelledby="privacy-sharing">
      <h2 id="privacy-sharing">{{ customerCopy.text('privacy.sharing-title') }}</h2>
      <p>{{ customerCopy.text('privacy.sharing') }}</p>
      <p>{{ customerCopy.text('privacy.preferences') }}</p>
    </section>

    <section aria-labelledby="privacy-control">
      <h2 id="privacy-control">{{ customerCopy.text('privacy.control-title') }}</h2>
      <p>{{ customerCopy.text('privacy.control') }}</p>
      <p>{{ customerCopy.text('privacy.deletion-receipt') }}</p>
      <p>{{ customerCopy.text('privacy.data-request') }}</p>
      <p><RouterLink to="/support">{{ customerCopy.text('privacy.support-link') }}</RouterLink></p>
    </section>
    </template>
    <section aria-labelledby="privacy-elevenlabs">
      <h2 id="privacy-elevenlabs">Hlas a záložní ilustrace ElevenLabs</h2>
      <p>Jednotný souhlas s AI při registraci nebo jeho jednorázovém obnovení pokrývá OpenAI i ElevenLabs. ElevenLabs dostává zvuk mikrofonu pro přepis a text odpovědí pro vytvoření hlasu. Přepis a přípravu hůlky zpracovává OpenAI. Před každým dalším hovorem se souhlas nevyžaduje znovu. Můžeš pokračovat psaním bez hlasového přenosu do ElevenLabs.</p>
      <p>Pokud se ilustrace hůlky u OpenAI nepodaří, může následovat jeden pokus přes ElevenLabs. Předává se jen vizuální popis uložené hůlky, nikoli fotografie, jméno hosta nebo celý rozhovor. Podrobnosti uvádějí <a href="https://elevenlabs.io/privacy-policy" target="_blank" rel="noopener noreferrer">zásady soukromí ElevenLabs</a>.</p>
    </section>
    <section aria-labelledby="privacy-push">
      <h2 id="privacy-push">Oznámení</h2>
      <p>Oznámení jsou dobrovolná a zapínají se zvlášť na každém zařízení. Pro doručení uložíme identifikátor zařízení nebo přihlášení prohlížeče a propojíme ho s aktuálním přihlášením k účtu. Stručné zprávy o výzvách do hry, pozvánkách a večeru doručují služby Apple, Google nebo poskytovatel prohlížeče. Obsah postavy ani rozhovorů do oznámení neposíláme.</p>
      <p>Oznámení vypneš v nastavení účtu nebo před začátkem večera v nabídce „Účet“. Po odhlášení, vypršení přihlášení nebo odebrání přístupu server další zprávy neodesílá; zpráva již předaná doručovací službě může ještě dorazit. Při odstranění účtu smažeme také jeho přihlášení k oznámením a čekající zprávy.</p>
    </section>
    <section aria-labelledby="privacy-video">
      <h2 id="privacy-video">Video při otevření večera</h2>
      <p>Při otevření večera nebo jako překvapení u bosse se může přehrát video uložené na našem serveru. Přehrávání nepoužívá YouTube ani jiný externí přehrávač. U překvapení u bosse si ukládáme, že už jsi ho dostal, aby se ti znovu nepřehrávalo.</p>
    </section>
  </article>
</template>
