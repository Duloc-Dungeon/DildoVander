import { accountTransport } from '../identity/session';

export interface AdminCharacterInput {
  name: string;
  world: string;
  version?: string | null;
  description?: string | null;
  language?: string | null;
}

export interface AdminCharacter {
  profileId: string;
  revision: number;
  userInput: AdminCharacterInput;
  status: string;
  enrichment: { summary: string } | null;
  errorCode: string | null;
  updatedAt: number;
  portrait: { status: string; hasImage: boolean; errorCode: string | null; updatedAt: number } | null;
}
export type AdminCharacterSummary = Pick<AdminCharacter, 'profileId' | 'revision' | 'userInput' | 'status' | 'updatedAt'>;
export interface AdminCharacterPage { items: AdminCharacterSummary[]; nextCursor?: string | null }

export const maxCharacterImageBytes = 8 * 1024 * 1024;
export const characterImageTypes = ['image/png', 'image/jpeg', 'image/webp'];
export const adminCharacterPath = (profileId: string) => `/app/v1/admin/characters/${encodeURIComponent(profileId)}`;

export async function uploadCharacterPortrait(character: AdminCharacter, file: File, signal: AbortSignal): Promise<AdminCharacter> {
  const response = await accountTransport(`${adminCharacterPath(character.profileId)}/portrait?expectedRevision=${character.revision}`, {
    method: 'PUT', body: file, signal, redirect: 'error',
    headers: { 'Content-Type': file.type, 'X-DV-Confirm': 'yes' },
  });
  const result = await response.json();
  if (!response.ok) throw Object.assign(new Error('Obrázek se nepodařilo nahrát.'), { status: response.status, code: result?.code });
  return result as AdminCharacter;
}

export function characterAdminError(cause: unknown): string {
  const { code, status } = (cause ?? {}) as { code?: string; status?: number };
  if (code === 'character_revision_conflict') return 'Postavu mezitím někdo změnil. Načtěte aktuální verzi a zkontrolujte ji před dalším uložením.';
  if (code === 'recent_passkey_required') return 'Před uložením znovu ověřte svou totožnost.';
  if (code === 'invalid_character_input') return 'Zkontrolujte jméno, svět a délku ostatních údajů.';
  if (status === 413) return 'Obrázek je příliš velký. Vyberte soubor do 8 MB.';
  if (status === 415 || code === 'invalid_character_portrait') return 'Soubor není podporovaný obrázek. Vyberte platný PNG, JPEG nebo WebP do 4096 × 4096 bodů a 16 milionů bodů celkem.';
  if (status === 404) return 'Postava už není dostupná. Obnovte seznam postav.';
  if (status === 401 || status === 403) return 'Správa postav vyžaduje přihlášeného vlastníka nebo správce.';
  return 'Změnu se nepodařilo potvrdit. Načtěte aktuální verzi a ověřte stav na serveru.';
}
