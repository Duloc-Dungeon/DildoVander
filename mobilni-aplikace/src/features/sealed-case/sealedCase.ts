import { accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';

export interface Plan {
  chapter: number; expectedActionRevision: number; confirmed: boolean; intent: number;
  targetId?: string | null; destination?: string | null; method?: string | null;
  condition?: { kind: number; evidenceId?: string } | null; fallback?: string;
  lethalConfirmed?: boolean; preparedIntent?: number | null; resourceId?: string | null; summary?: string | null; defensePolicy?: number | null; shelteredPosition?: boolean | null;
}
export interface PlayerCard { id: string; name: string; seat: string; state: string; location: string | null; mask: string | null; portraitUrl: string | null; isAi?: boolean }
export interface Item { id: string; name: string; ownerId: string; transferable: boolean }
export interface Clue { id: string; chain: string; family: string; candidates: string[]; sourceEvent: string }
export interface Choice { id: string; label: string; benefit: string; cost: string; risk: string; plan: Plan }
export interface Recap { chapter: number; actor: string; target: string | null; intent: string; result: string; eventRef: string; reason?: string | null; rolls?: { attack: number; defense: number; attackTotal: number; defenseTotal: number } | null; traceCreated?: boolean }
export interface Play {
  id: string; phase: string; serverNow: number; chapter: number; chapters: number;
  planningEndsAt: number | null; quietEndsAt: number | null; finaleEndsAt: number | null;
  expectedPlayers: number; readyPlayers: number; ownId: string; ownReady: boolean; ownScenarioConsent: boolean;
  canManage: boolean; canStart: boolean; canJoin?: boolean; canManageAi?: boolean; aiAvailable?: boolean; lobbyRevision: number | null; ownState: string;
  ownTeam: string | null; ownKit: string | null; teamMembers: string[]; location: string | null;
  locations: Record<string, string>; adjacentLocations: string[]; players: PlayerCard[];
  ownPlan: (Plan & { id: string; revision: number; actorId: string }) | null; choices: Choice[];
  clues: Clue[]; sources: { id: string; label: string }[]; ownOversight: boolean; ownItems: Item[]; visibleItems: Item[]; notes: string[];
  statements: { id: string; actor: string; body: string; recipients: string[]; public: boolean; createdAt: number }[];
  ownOutcomes: Recap[]; publicNarration?: { text: string }; privateNarration?: { text: string };
  finale: { phase: string; ballotSize: number; ownSubmitted: boolean; ownBallot: string[];
    runoffCandidates: string[]; runoffPlaces: number; selected: string[] | null; winner: string | null;
    conspirators: string[] | null; reconstruction: Recap[] | null } | null;
}
export class SealedError extends Error { constructor(public code: string, public status: number) { super(code); } }
export const operation = () => secureUuid();
export async function request<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await accountTransport(`/app/v1/sealed-cases${path}`, {
    method: body === undefined ? 'GET' : 'POST', signal,
    ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
  });
  if (!response.ok) { const problem = await response.json().catch(() => ({})); throw new SealedError(problem.code ?? 'sealed_case_retry', response.status); }
  return response.json();
}
export const intentNames = ['Vyčkat', 'Odpočívat', 'Chránit', 'Připravit se', 'Pátrat', 'Sledovat', 'Ukrást předmět',
  'Sabotovat doručení', 'Omráčit', 'Krajní čin', 'Pomoci', 'Prověřit vlastní stopu', 'Doručit vlastní svědectví',
  'Prozkoumat ozvěnu scény', 'Snová stopa', 'Maskovat se', 'Předstírat spánek', 'Předat předmět'];
export const phaseNames: Record<string, string> = { lobby: 'Příprava případu', introduction: 'Úvod případu', planning: 'Čas na plán', quiet: 'Klidový úsek', paused: 'Společná pauza', finale: 'Finále', finished: 'Závěrečná rekonstrukce' };
export function errorText(error: unknown): string {
  const code = error instanceof SealedError ? error.code : '';
  const copy: Record<string, string> = {
    sealed_case_owner_required: 'AI hráče může přidávat jen owner, který případ založil.',
    sealed_case_ai_unavailable: 'AI pro tento večer není zapnutá nebo nemá dostupné připojení. Žádné místo se nepřidalo.',
    sealed_case_invalid_ai_count: 'Zvol celý počet AI hráčů v mezích volných míst.',
    sealed_case_lobby_locked: 'Obsazení už je uzavřené. AI hráče do něj teď přidat nelze.',
    revision_conflict: 'Obsazení se mezitím změnilo. Obnov stav a porovnej počet volných míst.',
    sealed_case_character_required: 'Nejprve si ulož svou postavu.',
    sealed_case_lobby_full: 'Všechna místa už mají své hosty.',
    sealed_case_roster_invalidated: 'Obsazení se změnilo. Organizátor musí připravit nový případ.',
    sealed_case_scenario_consent_required: 'Každý hráč musí přijmout scénářovou minulost a závěrečné odhalení.',
    chapter_locked: 'Tato kapitola už plán nepřijímá. Platí poslední potvrzená verze.',
    wrong_chapter: 'Návrh patří do jiné kapitoly. Do další se sám nepřenese.',
    action_revision_conflict: 'Mezitím byla potvrzena jiná verze tvého plánu. Porovnej ji se svým návrhem.',
    sealed_case_ballot_closed: 'Toto hlasovací okno už je uzavřené.',
    sealed_case_invalid_ballot: 'Vyber přesný počet různých ostatních hráčů, nebo se zdrž hlasu.',
    case_not_found: 'Tento případ už není dostupný. Mohl být ukončen kvůli soukromí některého hráče.',
  };
  return copy[code] ?? 'Spojení se nevrátilo v pořádku. Uložený plán zůstává na serveru; návrh zatím neodesílej pod novým potvrzením.';
}

const intentCodes = ['Hold', 'Rest', 'Guard', 'Prepare', 'Investigate', 'Follow', 'Steal', 'Sabotage', 'Stun', 'Lethal', 'Help', 'EchoReview', 'EchoDeliver', 'EchoScene', 'Dream', 'Mask', 'FeignSleep', 'Transfer'];
export const intentText = (value: string) => intentNames[intentCodes.indexOf(value)] ?? 'Potvrzený záměr';
export const resultText = (value: string) => ({ Success: 'Provedeno', Defended: 'Zastaveno obranou', Hold: 'Zásah se neuskutečnil', RequestValid: 'Přijato k vyhodnocení' } as Record<string, string>)[value] ?? value;
export const reasonText = (value: string) => ({ NO_CONFIRMED_ACTION: 'Bez potvrzeného zásahu.', TARGET_UNREACHABLE: 'Cíl nebyl v dosahu.', TRIGGER_NOT_OBSERVED: 'Podmínka nebyla dostupným pozorováním splněna.', TARGET_HAS_NO_BODY: 'Cíl už neměl tělo.', FOLLOW_NOT_OBSERVED: 'Sledování nebylo opřené o dostupné pozorování.', NO_ACCESS: 'Přesun nebyl dostupný.', EXTREME_TOKEN_UNAVAILABLE: 'Krajní čin už nebyl dostupný.' } as Record<string, string>)[value] ?? 'V dané scéně nebyly splněné podmínky zásahu.';
