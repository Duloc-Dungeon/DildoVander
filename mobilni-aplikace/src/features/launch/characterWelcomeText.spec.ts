import { expect, it } from 'vitest';
import { characterWelcomeText } from './characterWelcomeText';

it.each([null, undefined, '', '  ', 'null', ' NULL ', 'undefined', {}, 42])('hides missing generated text: %j', value => {
  expect(characterWelcomeText(value)).toBe('');
});

it('preserves real copy, including words containing null', () => {
  expect(characterWelcomeText('  Anulujeme pouze tvé přehnané očekávání.  ')).toBe('Anulujeme pouze tvé přehnané očekávání.');
});
