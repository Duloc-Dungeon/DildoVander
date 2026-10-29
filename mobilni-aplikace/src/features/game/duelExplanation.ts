import { actionName, type Round } from './game';

const relations: Record<string, string> = {
  strike: 'Úder přeruší lest.',
  trick: 'Lest obejde štít.',
  shield: 'Štít zastaví úder.',
};

export function explainRound(round: Round): string {
  const choices = `${actionName(round.playerChoice)} proti ${actionName(round.opponentChoice)}.`;
  const bids = (round.playerSparkBid ?? 0) + (round.opponentSparkBid ?? 0);
  if (round.winner === 'draw') {
    return `${choices} Stejná kouzla: remíza, bez bodů.${bids ? ` Přisazené jiskry (${bids}) se spotřebovaly bez bodu.` : ''}`;
  }
  const playerWon = round.winner === 'player';
  const winningChoice = playerWon ? round.playerChoice : round.opponentChoice;
  const points = (playerWon ? round.playerPoints : round.opponentPoints) ?? round.points;
  const breakdown = points === undefined ? ''
    : bids && points === 1 + bids ? ` 1 za výměnu + ${bids} za jiskry = ${points}.`
      : ` Získané body: ${points}.${bids ? ` Přisazené jiskry: ${bids}.` : ''}`;
  return `${choices} ${relations[winningChoice] ?? ''} ${playerWon ? 'Výměnu získáváš ty.' : 'Výměnu získává soupeř.'}${breakdown}`;
}

export function duelHighlight(rounds: Round[]): string {
  const decisive = [...rounds].reverse().find(round => round.winner !== 'draw');
  if (!decisive) return rounds.length ? 'Ani jedna odhalená výměna nepřinesla bod.' : '';
  return `Výměna ${decisive.index}: ${explainRound(decisive)}`;
}
