import { describe, expect, it } from 'vitest';
import type { TestCharacter } from './contracts';
import { characterDisplayName } from './characterDisplayName';

const character: TestCharacter = {
  id: 'character-1',
  label: null,
  profile: {
    profileId: 'profile-1',
    revision: 1,
    userInput: { name: 'Alena', world: 'Vlastní svět', description: '' },
    status: 'ready',
  },
};

describe('characterDisplayName', () => {
  it('adds the optional label after the original name', () => {
    expect(characterDisplayName({ ...character, label: 'Moje verze' })).toBe('Alena · Moje verze');
  });

  it('uses the original name when the label is empty or absent', () => {
    expect(characterDisplayName(character)).toBe('Alena');
    expect(characterDisplayName({ ...character, label: '' })).toBe('Alena');
  });
});
