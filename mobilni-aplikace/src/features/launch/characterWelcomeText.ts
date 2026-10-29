import { pickVariant } from '../humor/visitHumor';
import type { PrelaunchCharacterResponse } from './generatedPrelaunchCharacter';

// Older generated responses can contain serialized missing values as text.
export function characterWelcomeText(value: unknown): string {
  if (typeof value !== 'string') return '';
  const text = value.trim();
  return /^(null|undefined)$/i.test(text) ? '' : text;
}

export function characterIntroductionText(artwork: PrelaunchCharacterResponse['artwork'], ready: boolean, roll = 0): string {
  const variants = artwork?.welcome?.jokes;
  const validVariants = Array.isArray(variants) && variants.length >= 3 && variants.length <= 6
    && variants[0] === artwork?.welcome?.joke && new Set(variants).size === variants.length
    && variants.every(value => typeof value === 'string' && value.length <= 120 && characterWelcomeText(value));
  const welcome = characterWelcomeText(validVariants ? pickVariant(variants, roll) : artwork?.welcome?.joke);
  const loading = characterWelcomeText(artwork?.loadingMessage);
  return ready ? welcome || loading || characterWelcomeText(artwork?.finalMessage) : loading || welcome;
}
