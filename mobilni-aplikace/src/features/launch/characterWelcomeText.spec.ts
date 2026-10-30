import { expect, it } from 'vitest';
import { characterIntroductionText, characterWelcomeText } from './characterWelcomeText';

it.each([null, undefined, '', '  ', 'null', ' NULL ', 'undefined', {}, 42])('hides missing generated text: %j', value => {
  expect(characterWelcomeText(value)).toBe('');
});

it('uses personal loading text during preparation and the saved verdict after reveal', () => {
  const artwork = { loadingMessage: 'První osobní hláška.', finalMessage: 'Osobní přivítání.', welcome: null };
  expect(characterIntroductionText(artwork, false)).toBe('První osobní hláška.');
  expect(characterIntroductionText(artwork, true)).toBe('Osobní přivítání.');
  expect(characterIntroductionText({ ...artwork, loadingMessage: 'null' }, false)).toBe('');
});

it('preserves real copy, including words containing null', () => {
  expect(characterWelcomeText('  Anulujeme pouze tvé přehnané očekávání.  ')).toBe('Anulujeme pouze tvé přehnané očekávání.');
});
