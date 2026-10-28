import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import GameRanking from './GameRanking.vue';
import type { Ranking } from './game';

const combat: Ranking[] = Array.from({ length: 12 }, (_, index) => ({
  id: `player-${index}`, displayName: index === 10 ? 'Eliška' : `Host ${index + 1}`,
  xp: 120 - index * 10, wins: 0, rank: index + 1,
}));

describe('game standings', () => {
  it('shows the leaders and the current player even near the end of the ranking', () => {
    const view = mount(GameRanking, { props: { combat, yolo: [], playerId: 'player-10' } });
    expect(view.findAll('li')).toHaveLength(12);
    expect(view.get('li[aria-current="true"]').text()).toContain('Eliška');
    expect(view.get('.game-ranking-position').text()).toContain('11. místo');
    expect(view.get('.game-ranking-position').text()).toContain('příčky: 10 XP');
    expect(view.findAll('li')[0].text()).toContain('120 XP');
  });

  it('filters names without diacritics without changing the saved rank or own position', async () => {
    const view = mount(GameRanking, { props: { combat, yolo: [], playerId: 'player-0' } });
    await view.get('input[type="search"]').setValue('eliska');
    expect(view.findAll('li')).toHaveLength(1);
    expect(view.get('li').attributes('value')).toBe('11');
    expect(view.get('.game-ranking-position').text()).toContain('1. místo');
    await view.get('input[type="search"]').setValue('nikdo');
    expect(view.text()).toContain('žádný hráč neodpovídá');
  });

  it('switches to independent YOLO wins and handles an empty discipline', async () => {
    const view = mount(GameRanking, { props: { combat, yolo: [], playerId: 'player-0' } });
    await view.get('input[value="yolo"]').setValue();
    expect(view.findAll('li')).toHaveLength(0);
    expect(view.text()).toContain('zatím nejsou žádní hráči');
    await view.setProps({ yolo: [{ id: 'player-0', displayName: 'Host', rank: 1, xp: 0, wins: 3 }] });
    expect(view.get('li').text()).toContain('3 výhry');
    expect(view.text()).toContain('nepřidávají bojovou sílu');
  });

  it('honors shared server ranks and reports the next strictly higher score', () => {
    const rows = [{ ...combat[0], rank: 1, xp: 100 }, { ...combat[1], rank: 2, xp: 50 }, { ...combat[2], rank: 2, xp: 50 }];
    const view = mount(GameRanking, { props: { combat: rows, yolo: [], playerId: 'player-2' } });
    expect(view.get('.game-ranking-position').text()).toContain('Dělíš se o 2. místo');
    expect(view.get('.game-ranking-position').text()).toContain('příčky: 50 XP');
    expect(view.findAll('li').map(row => row.attributes('value'))).toEqual(['1', '2', '2']);
  });
});
