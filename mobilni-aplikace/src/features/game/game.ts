import { accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';

export type Choice = 'strike' | 'trick' | 'shield';
export type GameMode = 'quick' | 'tactical' | 'yolo' | 'boss';
export const actions: { id: Choice; name: string; beats: string }[] = [
  { id: 'strike', name: 'Úder', beats: 'Přeruší lest' },
  { id: 'trick', name: 'Lest', beats: 'Obejde štít' },
  { id: 'shield', name: 'Štít', beats: 'Zastaví úder' },
];
export const actionName = (choice?: string | null) => actions.find(action => action.id === choice)?.name ?? '—';
export const modeName = (mode: string) => ({ quick: 'Rychlý duel', tactical: 'Taktický duel', yolo: 'YOLO lóže', boss: 'Baron Šarlatán' })[mode] ?? mode;
export interface Opponent { id: string; displayName: string; xp: number; matchesPlayed: number; hasWand?: boolean; wandTier?: number; tacticalUnlocked?: boolean }
export interface Inventory { strike: number; trick: number; shield: number }
export interface Round { index: number; playerChoice: string; opponentChoice: string; winner: string; playerSparkBid?: number; opponentSparkBid?: number; playerPoints?: number; opponentPoints?: number; points?: number; reason?: string }
export interface PartyTask { title: string; text: string; category: string; status: string; exchangeCount?: number }
export interface Match {
  id: string; mode: GameMode; status: string; revision: number; opponent: Opponent;
  rounds: Round[]; completedRounds: number; totalRounds: number; block: number;
  playerScore: number; opponentScore: number; playerSparks: number; opponentSparks: number;
  ownChoiceLocked: boolean; opponentChoiceLocked: boolean; loadout?: string;
  playerInventory?: Inventory; opponentInventory?: Inventory; canAccept?: boolean; canCancel?: boolean;
  tasksEnabled: boolean; adultConsent: boolean; flirtConsent: boolean;
  createdAt: number; completedAt?: number; result?: string; xpAwarded: number;
  partyTask?: PartyTask; yolo?: { status: string; beats: { text: string; detailRefs: string[] }[]; verdict: string; winnerId?: string };
  sparksEnabled?: boolean; playerEnergy?: number; opponentEnergy?: number;
  strength?: number; phase?: number; hint?: string; bossSpark?: boolean; exhibition?: boolean;
  seedCommitment?: string; seedReveal?: string;
  ownLoadoutLocked?: boolean; opponentLoadoutLocked?: boolean; rulesVersion?: string;
  needsLoadout?: boolean; loadoutsAvailable?: boolean;
}
export interface BossProfile { revealed: boolean; rescued: boolean; referenceXp: number; revision: number; activeAttemptId?: string }
export interface Milestone { id: string; title: string; text: string; action: string; destination: string }
export interface Ranking { id: string; displayName: string; xp: number; wins: number; rank: number }
export interface Progress {
  revision: number; tacticalMatches: number; keeperState: string;
  pending: Milestone[]; messages: Milestone[]; titles: string[];
  combatRanking: Ranking[]; yoloRanking: Ranking[];
}
export interface Lobby {
  player: Opponent & { tacticalUnlocked: boolean; yoloUnlocked: boolean; hasWand?: boolean; wandTier?: number };
  opponents: Opponent[]; incoming: Match[]; active: Match[]; history: Match[];
  boss?: BossProfile;
}
export interface TaskConsent { tasksEnabled: boolean; adultConsent: boolean; flirtConsent: boolean }
export class GameError extends Error {
  constructor(public status: number, public code: string) { super(code); }
}
export async function gameRequest<T>(path = '', body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await accountTransport(`/app/v1/game${path}`, {
    method: body === undefined ? 'GET' : 'POST', signal,
    ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new GameError(response.status, problem.code ?? problem.errorCode ?? 'game_unavailable');
  }
  return response.json();
}
export const operation = () => secureUuid();
export function gameErrorText(error: unknown): string {
  if (!(error instanceof GameError)) return 'Spojení se přerušilo. Obnov stav, než budeš pokračovat.';
  if (error.code === 'game_wand_required') return 'Nejdřív si s Olivanderem vyrob vlastní hůlku.';
  if (error.code === 'game_wand_upgrade_required') return 'Nejdřív si v dílně vylepši hůlku pro taktickou hru.';
  if (error.status === 401 || error.status === 403) return 'Přístup ke hře není dostupný. Vrať se přes platnou pozvánku.';
  if (error.status === 409) return 'Hra se mezitím změnila. Obnovujeme její skutečný stav.';
  if (error.status === 429) return 'Chvilku vyčkej a potom obnov stav.';
  return 'Hru se nepodařilo načíst. Zkus obnovit stav.';
}

export function practiceRound(choice: Choice, opponent: Choice): Round {
  const wins = { strike: 'trick', trick: 'shield', shield: 'strike' };
  const winner = choice === opponent ? 'draw' : wins[choice] === opponent ? 'player' : 'opponent';
  return { index: 0, playerChoice: choice, opponentChoice: opponent, winner, points: winner === 'draw' ? 0 : 1 };
}
