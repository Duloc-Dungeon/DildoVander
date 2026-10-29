import type { PrelaunchCharacterResponse } from './generatedPrelaunchCharacter';

// Older generated responses can contain serialized missing values as text.
export function characterWelcomeText(value: unknown): string {
  if (typeof value !== 'string') return '';
  const text = value.trim();
  return /^(null|undefined)$/i.test(text) ? '' : text;
}
export const characterIntroductionFallback = 'Postava dostane vlastní tvář. Zrcadlo si zatím vyjednává právo na jiný názor.';

export function characterIntroductionText(artwork: PrelaunchCharacterResponse['artwork'], ready: boolean): string {
  const welcome = characterWelcomeText(artwork?.welcome?.joke);
  const loading = characterWelcomeText(artwork?.loadingMessage);
  return ready ? welcome || loading || characterWelcomeText(artwork?.finalMessage) : loading || welcome;
}
