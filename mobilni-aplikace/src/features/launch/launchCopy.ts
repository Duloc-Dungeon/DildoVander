import { computed } from 'vue';
import { customerCopy } from '../../application/customerCopy';

const cs = {
  title: 'Něco se chystá', introduction: 'Začneme jménem a světem, ze kterého přicházíš. Velký nástup si necháme na potom.',
  teaser: 'Tvoje místo už je připravené. Opona má ovšem vlastní názor na dochvilnost.',
  invitation: 'Mám pozvánku', name: 'Jméno postavy', universe: 'Její svět',
  nameHint: 'Tvé jméno pro tento večer.', universeHint: 'Vlastní svět nebo fiktivní univerzum.',
  consent: 'Souhlasím s odesláním fiktivního jména a univerza poskytovatelům OpenAI pro obohacení postavy a vytvoření její ilustrace.',
  portrait: 'Připravit můj portrét', missingPortrait: 'Tvá postava je uložená. S tvým souhlasem jí teď připravíme i portrét.',
  loading: 'Portrét se připravuje. Dobré osvětlení má své nároky.',
  failed: 'Portrét se zatím nepodařilo připravit.', retry: 'Zkusit znovu',
  countdown: 'Každé kouzlo má svůj čas', countdownLabel: 'Čas do otevření',
  days: 'dní', hours: 'hodin', minutes: 'minut', seconds: 'sekund',
  soon: 'Tvůj okamžik se blíží.', unscheduled: 'Tvůj okamžik se blíží. Ještě se ladí poslední hvězda.',
  privacy: 'Ochrana soukromí', account: 'Soukromí a účet', support: 'Podpora',
  footnote: 'Trpělivost sluší i těm, kterým jinak nesluší vůbec nic.',
  portraitAlt: 'Portrét tvé postavy', home: 'Wand Night Stand, úvod',
  saved: 'Postava je bezpečně uložená. Tuhle stránku můžeš zavřít a později se vrátit.',
  hostNote: 'Zprávy pořadatele v původním jazyce',
};
const en: typeof cs = {
  title: 'Something is stirring', introduction: 'Start with a name and the world you come from. We will save the grand entrance for later.',
  teaser: 'Your place is ready. The curtain, however, has its own ideas about punctuality.',
  invitation: 'I have an invitation', name: 'Character name', universe: 'Their world',
  nameHint: 'Your name for the evening.', universeHint: 'An original world or fictional universe.',
  consent: 'I consent to sending the fictional name and universe to OpenAI providers to enrich the character and create an illustration.',
  portrait: 'Prepare my portrait', missingPortrait: 'Your character is saved. With your consent, we can prepare a portrait too.',
  loading: 'Your portrait is in progress. Good lighting has its demands.',
  failed: 'We could not prepare the portrait yet.', retry: 'Try again',
  countdown: 'Every spell has its moment', countdownLabel: 'Time until opening',
  days: 'days', hours: 'hours', minutes: 'minutes', seconds: 'seconds',
  soon: 'Your moment is getting closer.', unscheduled: 'Your moment is getting closer. One last star needs adjusting.',
  privacy: 'Privacy', account: 'Privacy and account', support: 'Support',
  footnote: 'Patience looks good on you. Annoying, we know.',
  portraitAlt: 'Portrait of your character', home: 'Wand Night Stand, home',
  saved: 'Your character is safely saved. You can close this page and come back later.',
  hostNote: 'Your host’s notes · shown in their original language',
};

export const launchCopy = computed(() => customerCopy.language.value === 'en' ? en : cs);
