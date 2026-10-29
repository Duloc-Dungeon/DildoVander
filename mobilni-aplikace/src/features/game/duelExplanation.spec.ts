import { describe, expect, it } from 'vitest';
import { duelHighlight, explainRound } from './duelExplanation';

describe('revealed duel evidence', () => {
  it('explains the actual winning relation and both spent bids', () => {
    expect(explainRound({ index: 1, playerChoice: 'strike', opponentChoice: 'shield', winner: 'opponent', playerSparkBid: 1, opponentSparkBid: 1, opponentPoints: 3 }))
      .toContain('Štít zastaví úder. Výměnu získává soupeř. 1 za výměnu + 2 za jiskry = 3.');
  });
  it('accounts for consumed bids in a tie without inventing a point', () => {
    expect(explainRound({ index: 1, playerChoice: 'shield', opponentChoice: 'shield', winner: 'draw', playerSparkBid: 1 }))
      .toContain('remíza, bez bodů. Přisazené jiskry (1) se spotřebovaly bez bodu.');
  });
  it('does not derive a boss damage total from ordinary duel points', () => {
    expect(explainRound({ index: 1, playerChoice: 'shield', opponentChoice: 'strike', winner: 'player', playerSparkBid: 1, playerPoints: 1.4 }))
      .toContain('Získané body: 1.4. Přisazené jiskry: 1.');
  });
  it('highlights a real exchange and handles an all-tie match honestly', () => {
    expect(duelHighlight([{ index: 2, playerChoice: 'trick', opponentChoice: 'shield', winner: 'player' }])).toContain('Výměna 2:');
    expect(duelHighlight([{ index: 1, playerChoice: 'strike', opponentChoice: 'strike', winner: 'draw' }])).toContain('Ani jedna');
    expect(duelHighlight([])).toBe('');
  });
});
