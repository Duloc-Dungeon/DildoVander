import type { TestCharacter } from './contracts';

export function characterDisplayName(character: TestCharacter): string {
  const name = character.profile.userInput.name;
  return character.label ? `${name} · ${character.label}` : name;
}
