import type { Clue, Plan, Play } from './sealedCase';

export const evidenceFamilies = ['A', 'B', 'C'] as const;

export function evidenceChains(clues: Clue[]) {
  const unique = [...new Map(clues.map(clue => [clue.id, clue])).values()];
  return [...new Set(unique.map(clue => clue.chain))].sort().map(id => {
    const parts = unique.filter(clue => clue.chain === id);
    const missing = evidenceFamilies.filter(family => !parts.some(clue => clue.family === family));
    // A repeated name or duplicate clue never completes a missing family.
    const candidates = missing.length ? [] : [...new Set(parts[0]!.candidates)]
      .filter(candidate => parts.every(clue => clue.candidates.includes(candidate)));
    return { id, parts, missing, candidates };
  });
}

export function shortestRoute(routes: Record<string, string[]> | undefined, start: string | null, goal: string): string[] | null {
  if (!routes || !start) return null;
  const queue = [[start]];
  const visited = new Set([start]);
  for (let index = 0; index < queue.length; index++) {
    const route = queue[index]!;
    const last = route.at(-1)!;
    if (last === goal) return route;
    for (const next of routes[last] ?? []) {
      if (!visited.has(next)) { visited.add(next); queue.push([...route, next]); }
    }
  }
  return null;
}

export function clueShare(clue: Clue, name: (id: string) => string) {
  return `Stopa ${clue.id} · řetězec ${clue.chain} · část ${clue.family}.\nOkruh kandidátů: ${clue.candidates.map(name).join(', ') || 'žádný'}.`;
}

export function sharedEvidence(play: Pick<Play, 'clues' | 'statements'>) {
  return play.statements.flatMap(statement => {
    // This is a player's claim, even when it uses our sharing format. Never
    // reconstruct authoritative evidence or merge candidate sets from its text.
    return [...statement.body.matchAll(/\bStopa ([\w-]{1,100}) · řetězec ([\w-]{1,100}) · část ([ABC])\./gu)].map(match => {
      const [, clueId, chain, family] = match;
      const ownClue = play.clues.find(clue => clue.id === clueId && clue.chain === chain && clue.family === family);
      return { key: `${statement.id}:${match.index}`, statement, clueId: clueId!, chain: chain!, family: family!, ownClue };
    });
  }).sort((a, b) => a.chain.localeCompare(b.chain) || a.family.localeCompare(b.family) || a.statement.createdAt - b.statement.createdAt);
}

export function planProblem(plan: Plan | undefined): string {
  if (!plan) return '';
  if (plan.intent === 0 && plan.destination) return 'Vyčkávání neumožňuje přesun. Zvol jiný typ akce, nebo zůstaň na místě.';
  if (plan.intent === 7 && !plan.resourceId) return 'Vyber jednu konkrétní stopu, kterou teď vidíš v tomto místě.';
  if (plan.intent === 7 && !plan.method) return 'Vyber metodu sabotáže.';
  if (plan.intent === 7 && !['DELAY', 'REDIRECT'].includes(plan.method ?? '')) return 'Vyber platnou metodu sabotáže.';
  if (plan.intent === 7 && plan.method === 'REDIRECT' && !plan.targetId) return 'Pro přesměrování vyber oprávněného adresáta.';
  if ([5, 6, 8, 9, 10, 12, 17].includes(plan.intent) && !plan.targetId) return 'Vyber postavu, na kterou má tah mířit.';
  if ([6, 12, 17].includes(plan.intent) && !plan.resourceId) return 'Vyber předmět nebo vlastní stopu k předání.';
  if (plan.intent === 3 && plan.preparedIntent == null) return 'Vyber, na jaký příští tah se připravuješ.';
  if (plan.intent === 3 && plan.preparedIntent === 4) return 'Příprava pátrání nemá herní účinek. Zvol jiný příští tah.';
  return '';
}

export function actionGuidance(intent: number, method?: string, targetId?: string): string {
  if (intent === 7) return method === 'REDIRECT'
    ? 'Vyber zdroj viditelný na tomto místě a oprávněného adresáta. Přesměrování mění jedno doručení; obsah a historii stopy nemění.'
    : 'Vyber jednu konkrétní stopu viditelnou na tomto místě. Zůstaneš u zdroje; přesun vyžaduje jiný tah. Zdržení změní novou dostupnost stopy pro pátrání do další kapitoly; již získané kopie zůstávají. Spotřebuje tah a může selhat. V poslední kapitole po úspěchu nikdo tuto stopu nově nezíská pátráním před hlasováním.';
  return ({
    0: 'Při vyčkávání se nepřesouváš. Chceš-li změnit místnost, zvol jiný typ akce.',
    4: 'Pátrání spotřebuje tento tah. Můžeš získat novou stopu, znovu narazit na známou, nebo tu nemusí být žádná nová dostupná stopa.',
    5: 'Sledování potřebuje dostupné pozorování cíle. Postavy se nejprve přesunou; nynější poloha nezaručuje dosah.',
    6: 'Vyber cizí předmět a jeho držitele. Krádež se vyhodnotí až po společných přesunech.',
    8: 'Vyber postavu a prostředek. Dosah se ověří až po společných přesunech.',
    9: 'Vyber postavu a prostředek. Krajní čin vyžaduje samostatné výslovné potvrzení; dosah se ověří až po přesunech.',
    10: 'Pomoc probouzí postavu v bezvědomí. Není to ochrana ani pomoc s pátráním. Cíl musí být po přesunech v dosahu; jeho skrytý stav předem neznáš.',
    12: 'Předáváš vlastní doloženou stopu vybrané známé postavě. Rozhovor je pouze tvrzení a stopu do jejího spisu nepřidá.',
    17: 'Vyber vlastní přenositelný předmět a příjemce. Po přesunech musíte být na stejném místě.',
  } as Record<number, string>)[intent] ?? (intent === 2 ? (targetId
    ? 'Ochrana vybrané postavy spotřebuje hlavní tah. Po přesunech se použije, jen pokud jste ve stejné místnosti; blokuje tam cizí sabotáž. Sama nedokazuje pokus o útok.'
    : 'Chránit sebe spotřebuje hlavní tah. Po společném přesunu chrání tvou přítomnou postavu v této kapitole před cizí sabotáží ve stejné místnosti; samo o sobě nedokazuje, že se někdo o sabotáž pokusil.') : '');
}
