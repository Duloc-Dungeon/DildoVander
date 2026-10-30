import { gameRequest, operation } from '../game/game';
import { RULES_VERSION } from './sparkEngine';

export interface SparkRank { id: string; displayName: string; score: number; rank: number }
export interface SparkStandings { rulesVersion: string; playerId: string; groupId: string; best: number; ranking: SparkRank[] }
export interface SparkTicket { runId: string; seed: number; rulesVersion: string; expiresAt: number }
export interface SparkReceipt { score: number; best: number; newBest: boolean }
export interface SparkReplay { endTick: number; taps: number[] }
export const readSparkStandings = (signal?: AbortSignal) => gameRequest<SparkStandings>('/arcade', undefined, signal);
export async function issueSparkTicket(signal?: AbortSignal, operationId = operation()): Promise<SparkTicket> {
  const ticket = await gameRequest<SparkTicket>('/arcade/runs', { operationId }, signal);
  if (ticket.rulesVersion !== RULES_VERSION || !Number.isInteger(ticket.seed)
    || ticket.seed < 0 || ticket.seed > 0xffffffff || ticket.expiresAt <= Date.now()) throw new Error('Unsupported spark rules');
  return ticket;
}
export const finishSparkRun = (runId: string, replay: SparkReplay, signal?: AbortSignal) =>
  gameRequest<SparkReceipt>(`/arcade/runs/${encodeURIComponent(runId)}/finish`, replay, signal);
