import { describe, expect, it } from 'vitest';
import type { Clue, Play } from './sealedCase';
import { clueShare, evidenceChains, planProblem, sharedEvidence, shortestRoute } from './sealedDossier';

const clue = (family: string, candidates = ['two', 'three'], chain = 'R01'): Clue => ({
  id: `${chain}-${family}`, chain, family, candidates, sourceEvent: 'private-source',
});

describe('investigation dossier', () => {
  it('requires all three distinct families of the same chain before intersecting candidates', () => {
    const a = clue('A');
    const partial = evidenceChains([a, a, clue('B'), clue('C', ['two'], 'R02')]);
    expect(partial[0]!.missing).toEqual(['C']);
    expect(partial[0]!.parts).toHaveLength(2);
    expect(partial[0]!.candidates).toEqual([]);
    const complete = evidenceChains([a, clue('B', ['two', 'four']), clue('C', ['two', 'five'])]);
    expect(complete[0]!.missing).toEqual([]);
    expect(complete[0]!.candidates).toEqual(['two']);
  });

  it('does not select a candidate when complete evidence has no common candidate', () => {
    expect(evidenceChains([clue('A', ['two']), clue('B', ['three']), clue('C', ['four'])])[0]!.candidates).toEqual([]);
  });

  it('finds the shortest public route, tolerates cycles, and never invents absent paths', () => {
    const routes = { VESTIBUL: ['SALON'], SALON: ['VESTIBUL', 'ARCHIV', 'KOMORA'], ARCHIV: ['SALON'], KOMORA: ['SALON'] };
    expect(shortestRoute(routes, 'VESTIBUL', 'KOMORA')).toEqual(['VESTIBUL', 'SALON', 'KOMORA']);
    expect(shortestRoute(routes, 'SALON', 'SALON')).toEqual(['SALON']);
    expect(shortestRoute(routes, 'VESTIBUL', 'OUTSIDE')).toBeNull();
    expect(shortestRoute(undefined, 'VESTIBUL', 'KOMORA')).toBeNull();
  });

  it('keeps a forged shared clue as a claim, even if its identifier matches an owned clue', () => {
    const owned = clue('A');
    const body = clueShare({ ...owned, candidates: ['invented'] }, id => id);
    const play = { clues: [owned], statements: [{ id: 'message', actor: 'other', body, public: false, recipients: [], createdAt: 1 }] } satisfies Pick<Play, 'clues' | 'statements'>;
    const result = sharedEvidence(play);
    expect(result).toHaveLength(1);
    expect(result[0]!.ownClue).toEqual(owned);
    expect(result[0]!.statement.body).toContain('invented');
    expect(evidenceChains(play.clues)[0]!.candidates).toEqual([]);
    expect(clueShare(owned, id => id)).not.toContain('private-source');
  });

  it.each(['Vlastní věta. ', 'Vlastní věta.\n\n'])('recognizes every shared reference after custom text (%j)', prefix => {
    const owned = clue('A');
    const body = `${prefix}${clueShare(clue('B'), id => id)}\n\n${clueShare(owned, id => id)}`;
    const statement = { id: 'two-references', actor: 'other', body, public: true, recipients: [], createdAt: 1 };
    const play = { clues: [owned], statements: [statement] };
    const result = sharedEvidence(play);
    expect(result.map(claim => claim.family)).toEqual(['A', 'B']);
    expect(new Set(result.map(claim => claim.key)).size).toBe(2);
    expect(result.every(claim => claim.statement === statement)).toBe(true);
    expect(result[0]!.ownClue).toEqual(owned);
    expect(result[1]!.ownClue).toBeUndefined();
    expect(evidenceChains(play.clues)[0]!.missing).toEqual(['B', 'C']);
  });

  it('blocks missing public action parameters without testing the secret state of a target', () => {
    const base = { chapter: 1, expectedActionRevision: 0, confirmed: true, intent: 7 };
    expect(planProblem(base)).toContain('konkrétní stopu');
    expect(planProblem({ ...base, resourceId: 'R01-A', method: 'DELAY' })).toBe('');
    expect(planProblem({ ...base, resourceId: 'R01-A', method: 'REDIRECT' })).toContain('adresáta');
    expect(planProblem({ ...base, resourceId: 'R01-A', method: 'REDIRECT', targetId: 'two' })).toBe('');
    expect(planProblem({ ...base, intent: 10 })).toContain('postavu');
    expect(planProblem({ ...base, intent: 10, targetId: 'unknown-state' })).toBe('');
    expect(planProblem({ ...base, intent: 4 })).toBe('');
    expect(planProblem({ ...base, intent: 0 })).toBe('');
    expect(planProblem({ ...base, intent: 0, destination: 'SALON' })).toContain('Vyčkávání neumožňuje přesun');
    expect(planProblem({ ...base, intent: 3, preparedIntent: 4 })).toContain('Příprava pátrání nemá herní účinek');
    expect(planProblem({ ...base, intent: 3, preparedIntent: 6 })).toBe('');
  });
});
