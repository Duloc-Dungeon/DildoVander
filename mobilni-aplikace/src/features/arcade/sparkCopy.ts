import type { SparkState } from './sparkEngine';

export const gatesLabel = (score: number) => `${score.toLocaleString('cs-CZ')} ${score === 1 ? 'průlet' : score >= 2 && score <= 4 ? 'průlety' : 'průletů'}`;

export function progressionHint(score: number): string {
  if (score < 15) return 'Čti sestavy a připravuj si další průlet.';
  if (score < 20) return 'Kolejnice ukazují, kam se otvor pohne.';
  if (score < 35) return 'Plný kosočtverec otiskne tvou polohu o dvě brány dál.';
  if (score < 60) return 'Dvojice regálů se pohybují společně.';
  if (score < 90) return 'Protiběžná dvojice: připrav si obrat mezi otvory.';
  if (score < 100) return 'Dlouhý průlet se hýbe. Sleduj ho až do výjezdu.';
  return 'Dvě ozvěny: řeš pečeť a připravuj další průlet.';
}

/** Original Czech copy: deadpan, literalism and anticlimax from the Spaces guide.
 * Stable within a result; never announce jokes on every simulation update. */
export function resultCopy(state: SparkState, newBest: boolean) {
  if (state.status === 'complete') return { title: 'Sklad proletěn.', joke: 'Kouzlo došlo až na konec skladu. Tam se poprvé ukázalo, že nemá další plán.', tip: 'Celá třicetiminutová trať je za tebou.' };
  const kind = state.collision?.kind;
  const tip = kind === 'wall' ? 'Příště obrať směr dřív, než jiskra doletí ke stěně.'
    : kind === 'echo' ? 'Pečeť vznikla tam, kudy jsi proletěl o dvě brány dřív. Příště si připrav cestu kolem ní.'
    : kind === 'corridor' ? 'V dlouhém průletu sleduj pohyb mezery, dokud neproletí celá police.'
      : 'Sleduj i další otvor. Obrat si připrav ještě před průletem.';
  if (newBest) return { title: 'Nový osobní rekord.', joke: 'Jiskra se dostala dál než kdy dřív. Pověst skladu jako bezpečného pracoviště nikoli.', tip };
  if (state.score === 0) return { title: 'Tady kouzlo skončilo.', joke: 'Kouzlo mělo velké plány. Police měla pevné uchycení.', tip };
  if (kind === 'wall') return { title: 'Stěna neuhnula.', joke: 'Jiskra se rozhodla jít vlastní cestou. Stavební plán s ní nepočítal.', tip };
  if (kind === 'echo') return { title: 'Vlastní průšvih.', joke: 'Sklad ti vyhověl a nechal překážku přesně tam, kde sis ji objednal.', tip };
  if (kind === 'corridor') return { title: 'Trochu těsná známost.', joke: 'Mezi regály to zajiskřilo. Regály trvají na tom, že z jejich strany šlo o čistě pracovní vztah.', tip };
  return { title: 'Police měla poslední slovo.', joke: 'Příručka bezpečné magie zachytila jiskru hřbetem. Konečně praktické využití.', tip };
}
