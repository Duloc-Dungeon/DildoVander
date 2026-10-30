import { computed, inject, ref, watch } from 'vue';
import { routerKey } from 'vue-router';

export const humorVariants = {
  portrait: ['Portrét: způsob, jak udělat dojem a ani se přitom nepohnout.', 'Portrét zachytí i prchavý dojem. Rám mu znemožní útěk.', 'Na portrétu můžeš hledět do budoucnosti. Bez povinnosti tam dojít.'],
  sharedLead: ['Dělíte se o první místo. Na vrcholu je těsno hlavně sebevědomím.', 'První místo je společné. Každý si může myslet, že patří hlavně jemu.', 'Na vrcholu nejde výš. Zato se tam dá ještě trochu posunout.'],
  soloLead: ['Jsi v čele. Zatím.', 'První místo máš. Teď už se dá stoupat jen v očích publika.', 'Vedeš. Soupeři tomu říkají průběžný stav.'],
  emptyRanking: ['První místo je volné. Zatím si na něj nikdo nestoupl.', 'Žebříček čeká na hráče. Bez nich se mu špatně povyšuje.', 'Nikdo není poslední. Soutěž zatím dosahuje neobvyklé spokojenosti.'],
  chronicle: ['Kronikář má připravený slavný úvod. Chybí mu už jen důvod.', 'Kronikář začal slovy „Toho památného dne“. Datum zatím píše tužkou.', 'Tvá kronika je bez poskvrny. Pomáhá jí, že je prázdná.'],
  upgrade: ['Hůlka povyšuje. Na vedoucí dřevo.', 'Hůlka má před sebou velkou budoucnost. Drž ji za správný konec.', 'Vyšší úroveň hůlky. Odpovědnost se bohužel nepřenáší na dřevo.'],
  novice: ['Tři kouzla. Na nerozhodnost až dost.', 'Tři kouzla, spousta možností. Včetně té špatné.', 'Hůlka umí tři kouzla. Výmluvy zůstávají na tobě.'],
  rescued: ['Vždycky jsem ti věřil. Skoro.', 'Věřil jsem ve svůj návrat. Tvoje účast mě příjemně překvapila.', 'Dílna je zase moje. Skromně připouštím, že i tvou zásluhou.'],
  victory: ['Vítězství ti sluší. Skromnost může počkat.', 'Vítězství: nejlepší důkaz, že to byl celou dobu plán.', 'Dnes máš navrch. Pro jistotu si ulož skóre.'],
  defeat: ['Dnes vítězí soupeř. Tvá odveta má konečně důvod.', 'Porážka rozšiřuje obzory. Zvlášť pohled na záda vítěze.', 'Dnes vyhrává soupeř. Zkušenost sis ovšem odnesl bez soutěže.'],
  draw: ['Remíza: vítězství, na kterém se nikdo neshodl.', 'Obě strany mají stejně bodů. Každá lepší vysvětlení.', 'Vítěz se nenašel. Zájemci byli dva.'],
  duelHistory: ['Paměť časem přikrášluje. Skóre nemá představivost.', 'Časem z každého těsného zápasu vznikne jasná převaha. Tady zůstávají čísla.', 'Vzpomínky můžeš převyprávět. Body mají vlastní názor.'],
  cases: ['Případ se uzavřel. Otázka, kdo měl celou dobu pravdu, má delší životnost.', 'Uzavřený případ: místo, kde už se nic neděje, zato se o všem dál mluví.', 'Záhada má konec. Vysvětlení, proč to bylo od začátku jasné, teprve začínají.'],
  silence: ['Ticho je podezřelé, ale k výslechu se zatím nedostavilo.', 'Ticho nic neprozradí. V tom je zatím ze všech nejlepší.', 'Zatím tu panuje shoda. Nikdo ještě nic neřekl.'],
  archive: ['Archiv považuje prázdnou polici za důkaz, že má ve všem pořádek.', 'Archiv má místo na velké činy. Zatím hlavně to místo.', 'Žádná památka se neztratila. Ještě nebylo co ztratit.'],
} as const satisfies Record<string, readonly [string, string, string]>;

export function pickVariant<T>(variants: readonly T[], roll: number): T | undefined {
  if (!variants.length) return undefined;
  return variants[Math.min(variants.length - 1, Math.max(0, Math.floor(roll * variants.length)))];
}

// Route entry, not render/polling, is the lifetime of a selection. Watching the
// path also handles reused and KeepAlive views without timers or persistent data.
export function useVisitHumor() {
  const router = inject(routerKey, null);
  const roll = ref(Math.random());
  if (router) watch(() => router.currentRoute.value.path, () => { roll.value = Math.random(); });
  const copy = computed(() => Object.fromEntries(Object.entries(humorVariants)
    .map(([key, variants]) => [key, pickVariant(variants, roll.value)!])) as Record<keyof typeof humorVariants, string>);
  return { copy, roll };
}
