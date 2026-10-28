// Loopback preview only. These synthetic responses never enter the app bundle.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const dist = path.resolve(fileURLToPath(new URL('../../dist/', import.meta.url)));
const id = 'a'.repeat(32);
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.webp': 'image/webp', '.png': 'image/png', '.ttf': 'font/ttf' };
export const gameStates = ['lobby', 'challenge', 'incoming', 'choice', 'waiting', 'result', 'early-result', 'adult', 'task-without-wand', 'yolo', 'error', 'empty', 'long', 'tactical', 'loadout', 'boss', 'boss-setup', 'captured', 'rescue', 'teams', 'team-choice', 'team-half', 'messages', 'ranking', 'ranking-long', 'ranking-empty', 'ranking-error', 'first-wand', 'upgrade-wand'];
function match(state) {
  if (state === 'task-without-wand') state = 'adult';
  return { id, mode: state === 'yolo' ? 'yolo' : ['tactical','loadout'].includes(state) ? 'tactical' : state === 'boss' ? 'boss' : 'quick', status: ['result', 'early-result', 'adult', 'yolo'].includes(state) ? 'complete' : state === 'incoming' ? 'incoming' : 'active',
    revision: 3, canAccept: state === 'incoming', canCancel: false,
    opponent: { id: 'two', displayName: state === 'long' ? 'Eliška z NeuvěřitelněDlouhéhoKouzelnickéhoRodu' : 'Eliška', xp: 24, matchesPlayed: 3 },
    rounds: ['result', 'adult'].includes(state) ? [{ index: 1, playerChoice: 'trick', opponentChoice: 'shield', winner: 'player', playerPoints: 2, opponentPoints: 0 }] : [],
    completedRounds: state === 'early-result' ? 4 : ['result', 'adult'].includes(state) ? 7 : 0, totalRounds: ['tactical','loadout'].includes(state) ? 8 : state === 'boss' ? 12 : 7, block: 1,
    playerScore: state === 'early-result' ? 4 : state === 'adult' ? 3 : state === 'result' ? 5 : 0, opponentScore: state === 'adult' ? 5 : state === 'result' ? 3 : 0,
    playerSparks: 2, opponentSparks: 2, sparksEnabled: true, ownChoiceLocked: state === 'waiting', opponentChoiceLocked: false,
    playerInventory: {strike:2,trick:1,shield:0}, opponentInventory: {strike:1,trick:2,shield:1}, needsLoadout: state === 'loadout',
    playerEnergy: 4, opponentEnergy: 2, strength: 1.2, phase: 2, hint: 'Baron upřednostňuje Úder (2/3). Štít si šetři na výměnu s jiskrou.', bossSpark: true,
    tasksEnabled: true, adultConsent: true, flirtConsent: true, createdAt: 1,
    result: state === 'adult' ? 'loss' : ['result','early-result','yolo'].includes(state) ? 'win' : undefined, xpAwarded: state === 'adult' ? 6 : ['result','early-result'].includes(state) ? 10 : 0,
    partyTask: state === 'adult' ? { title: 'Reklama po půlnoci', text: 'Předveď dvacetisekundovou reklamu na svou hůlku. Dvojsmysly vítány, publikum nic nemusí.', category: 'adult', status: 'pending', exchangeCount: 0 } : undefined,
    yolo: state === 'yolo' ? { status: 'complete', beats: ['Eliška vstupuje do arény s Děrovačkou osudu. Naproti stojí Mira s diplomatickým štítem.', 'Mira podává protest. Eliška jej děruje ještě před přečtením. Křivopísk se tváří, že to měl v pravidlech.', 'Mira získává cenu za elegantní stížnost. Eliška vítězí administrativním knockoutem.'].map(text => ({text,detailRefs:[]})), verdict: 'Eliška vítězí díky Děrovačce osudu.' } : undefined };
}
function teamMatch(state) {
  return { id, status: state === 'team-half' ? 'halftime' : 'active', revision: 4,
    teamMembers:['one','two'], opponents:['three','four'], acceptedBy:['one','two','three','four'],
    rounds:[], teamScore:3, opponentScore:2, completedRounds:state === 'team-half' ? 4 : 1,
    ownSparks:2, ownInventory:{strike:2,trick:2,shield:2}, ownChoiceLocked:false, allFourLocked:[false,false,false,false],
    halftimeEndsAt:Math.floor(Date.now()/1000)+15, xpAwarded:0, sparkBalances:{one:2,two:1,three:2,four:2}, ownTransferLocked:false, onTeamA:true };
}
export async function createGameFixtureServer(port = 0) {
  const requests = [];
  const taskOutcomes = new Map();
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    const state = /(?:^|; )game-fixture=([^;]+)/.exec(request.headers.cookie ?? '')?.[1] ?? 'lobby';
    const matchSnapshot = () => {
      const snapshot = match(state);
      if (snapshot.partyTask && taskOutcomes.has(state)) snapshot.partyTask.status = taskOutcomes.get(state);
      return snapshot;
    };
    const json = (value, status = 200) => { response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(value)); };
    if (url.pathname.startsWith('/__game/')) {
      const selected = url.pathname.split('/').at(-1); if (!gameStates.includes(selected)) return json({}, 404);
      taskOutcomes.delete(selected);
      const destinations = {challenge:'/play/challenge',lobby:'/play',empty:'/play','boss-setup':'/play/boss',boss:`/play/boss/${id}`,captured:'/play/keeper',rescue:'/play/keeper',teams:'/play/teams','team-choice':`/play/team/${id}`,'team-half':`/play/team/${id}`,messages:'/play/messages',ranking:'/play/ranking','first-wand':'/play','upgrade-wand':'/play/keeper'};
      const destination = destinations[selected] ?? `/play/match/${id}`;
      response.writeHead(302, { Location: selected.startsWith('ranking-') ? '/play/ranking' : destination, 'Set-Cookie': `game-fixture=${selected}; Path=/; SameSite=Strict` }); response.end(); return;
    }
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'one', displayName: 'Mira', roles: ['Customer'], openAiConsent: true }, csrfToken: 'synthetic' });
    if (url.pathname === '/app/v1/customer/wand') return json({hasCharacter: true, wand: ['first-wand', 'task-without-wand'].includes(state) ? null : {wandId:'synthetic-saved-wand'}, tier: ['first-wand', 'task-without-wand'].includes(state) ? 0 : state === 'upgrade-wand' ? 1 : 2});
    if (url.pathname === '/app/v1/customer/access') return json({ state: 'active', groupId: 'synthetic', accessRevision: 1 });
    if (url.pathname === '/app/v1/launch') return json({ allowed: true });
    if (url.pathname === '/app/v1/game') return json({ player: { id: 'one', displayName: 'Mira', xp: 40, matchesPlayed: 5, tacticalUnlocked: true, yoloUnlocked: true, hasWand: state !== 'first-wand', wandTier: state === 'upgrade-wand' ? 1 : 2 },
      opponents: state === 'empty' ? [] : [{ id: 'two', displayName: 'Eliška', xp: 40, matchesPlayed: 5 },{id:'three',displayName:'Jáchym',xp:40,matchesPlayed:5},{id:'four',displayName:'Barbora',xp:40,matchesPlayed:5}], incoming: [], active: [], history: [],boss:{revealed:state.includes('boss') || ['captured','rescue'].includes(state),rescued:state === 'rescue',referenceXp:40,revision:1} });
    if (url.pathname === '/app/v1/game/progress') {
      if (state === 'ranking-error') return json({ code: 'unavailable' }, 503);
      const combatRanking = state === 'ranking-empty' ? [] : state === 'ranking-long'
        ? Array.from({ length: 12 }, (_, index) => ({ id: index === 10 ? 'one' : `player-${index}`, displayName: index === 10 ? 'Míra z NeuvěřitelněDlouhéhoKouzelnickéhoRodu' : `Host ${index + 1}`, xp: 120 - index * 10, wins: 0, rank: index + 1 }))
        : [{id:'one',displayName:'Mira',xp:40,wins:0,rank:1},{id:'two',displayName:'Eliška',xp:30,wins:0,rank:2}];
      return json({ revision:1,tacticalMatches:3,keeperState:state === 'captured' ? 'captured' : state === 'rescue' ? 'rescued' : 'available',pending:[],titles:['Soupeř s pamětí'],messages:[{id:'sparks',title:'Malá jiskra. Velká drzost.',text:'Když prohraješ výměnu, její bod získá soupeř.',action:'Vyzkoušet jiskry',destination:'/play/challenge'}],combatRanking,yoloRanking:state === 'ranking-empty' ? [] : [{id:'one',displayName:'Mira',xp:0,wins:2,rank:1}] });
    }
    if (url.pathname === '/app/v1/game/boss/attempts' && request.method === 'POST') {
      response.setHeader('Set-Cookie', 'game-fixture=boss; Path=/; SameSite=Strict');
      return json(match('boss'));
    }
    if (url.pathname === '/app/v1/game/boss') return json({revealed:true,rescued:false,referenceXp:40,revision:1});
    if (url.pathname === '/app/v1/game/teams') return json({teams:[teamMatch(state)]});
    if (url.pathname.startsWith('/app/v1/game/teams/')) {
      if (request.method === 'POST') return json({...teamMatch(state),ownChoiceLocked:true,ownTransferLocked:true,revision:5});
      return json(teamMatch(state));
    }
    if (url.pathname.startsWith('/app/v1/game/matches/') || url.pathname.startsWith('/app/v1/game/boss/attempts/')) {
      if (state === 'error') return json({ code: 'unavailable' }, 503);
      if (request.method === 'POST') {
        const chunks = []; for await (const chunk of request) chunks.push(chunk);
        const body = JSON.parse(Buffer.concat(chunks).toString());
        requests.push({ path: url.pathname, body });
        if (url.pathname.endsWith('/task')) {
          taskOutcomes.set(state, body.taskAction === 'done' ? 'done' : 'skipped');
          return json({ ...matchSnapshot(), revision: 4 });
        }
        return json({ ...matchSnapshot(), ownChoiceLocked: true, revision: 4 });
      }
      return json(matchSnapshot());
    }
    if (/^\/(app\/v1|v1|guest)\//.test(url.pathname)) return json({});
    let file = path.resolve(dist, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
    if (!file.startsWith(dist + path.sep)) return json({}, 404);
    try { await stat(file); } catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream' }); response.end(await readFile(file));
  });
  await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
  return { server, origin: `http://127.0.0.1:${server.address().port}`, requests };
}
