import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { account, accountOrigin } from '../identity/session';
import { PersonaLabClient } from '../persona-lab/client';
import { LabVoice, type VoicePhase } from '../persona-lab/voice';
import type { LabSession } from '../persona-lab/contracts';
import { TestingError, TestingSessionsClient } from './client';
import { diagnosticRequestId } from '../../application/requestDiagnostics';
import type { CharacterInput, InputMode, TestCharacter, TestSession, TestSessionDetail } from './contracts';

export function useTestingSessions() {
  const route = useRoute(); const router = useRouter();
  const identity = () => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : '';
  const client = new TestingSessionsClient(identity); const lab = new PersonaLabClient(identity);
  const progressClient = new TestingSessionsClient(identity);
  const sessions = shallowRef<TestSession[]>([]); const characters = shallowRef<TestCharacter[]>([]);
  const selected = shallowRef<TestSessionDetail | null>(null);
  const error = ref(''); const notice = ref(''); const busy = ref(false); const loading = ref(false); const stale = ref(false);
  const errorRequestId = ref('');
  const errorRejected = ref(false); const clock = ref(Date.now());
  let errorFromRefresh = false;
  const inputMode = ref<InputMode>('text'); const consent = ref(false);
  const text = ref(''); const outputEnabled = ref(false); const captureEnabled = ref(false); const voicePhase = ref<VoicePhase>('idle');
  const partial = ref({ user: '', assistant: '' }); const spokenBusy = ref(false); const providerConfirmed = ref(false);
  const turnPhase = ref<'idle' | 'waiting' | 'finalizing' | 'recovering'>('idle');
  const uncertainTurn = ref(false);
  const progressPaused = ref(false);
  const permitted = computed(() => account.value?.roles.includes('Tester') === true);
  const voiceActive = computed(() => ['connecting', 'waiting', 'listening', 'speaking'].includes(voicePhase.value));
  const open = computed(() => selected.value?.session.status === 'open');
  let epoch = 0; let readEpoch = 0; let disposed = false;
  let refreshPending: { epoch: number; request: number; collections: boolean; promise: Promise<void> } | undefined;
  let nextCollectionRefreshAt = 0;
  let operationId: string | null = null; let pendingSpokenText = ''; let retiringSessionId:string|null=null;
  let pendingTextTurn: { sessionId: string; conversationId: string; input: string; priorTurnIds: Set<string> } | undefined;
  let voice = makeVoice();
  function makeVoice(): LabVoice {
    const currentEpoch = epoch; const currentId = selected.value?.session.id;
    const current = () => !disposed && epoch === currentEpoch && selected.value?.session.id === currentId;
    return new LabVoice({
      phase: value => { if (current()) { voicePhase.value = value; if(value === 'listening') { if(captureEnabled.value)providerConfirmed.value=true; if(pendingSpokenText) { if(voice.sendText(pendingSpokenText)) { pendingSpokenText = ''; text.value = ''; } } else if(!captureEnabled.value)spokenBusy.value=false; } } },
      transcript: (role, delta) => { if (current()) partial.value = { ...partial.value, [role]: (partial.value[role] + delta).slice(-32000) }; },
      turn: turn => {
        if (!current() || !selected.value) return;
        updateConversation({ ...selected.value.conversation, turns: [...selected.value.conversation.turns.filter(item => item.id !== turn.id), turn] });
        partial.value = { ...partial.value, [turn.role]: '' };
      },
      session: value => { if (current()) { updateConversation(value); if(!captureEnabled.value)providerConfirmed.value=false; } },
      providerReady: () => { if (current()) providerConfirmed.value = true; },
      interrupted: () => { if (current()) partial.value = { ...partial.value, assistant: '' }; },
      error: code => { if (current()) { recordError({ code }); spokenBusy.value = false; } },
      closed: () => { if (current()) {spokenBusy.value = false; void refresh();} },
    });
  }
  function updateConversation(conversation: LabSession): void {
    if (selected.value?.conversation.id === conversation.id) selected.value = { ...selected.value, conversation };
  }
  function stopVoice(): void { if(voiceActive.value && selected.value)retiringSessionId=selected.value.session.id; pendingSpokenText = ''; spokenBusy.value = false; providerConfirmed.value = false; voice.dispose(); captureEnabled.value = false; voicePhase.value = 'idle'; partial.value = { user: '', assistant: '' }; }
  function invalidate(identityChanged = false): void {
    epoch++; readEpoch++; client.invalidate(identityChanged); progressClient.invalidate(identityChanged); lab.invalidate(); stopVoice();
    selected.value = null; sessions.value = []; characters.value = []; error.value = ''; notice.value = '';
    errorRequestId.value = ''; errorRejected.value = false; errorFromRefresh = false; stale.value = false;
    text.value = ''; busy.value = false; loading.value = false; operationId = null; retiringSessionId=null; pendingTextTurn=undefined; uncertainTurn.value=false; turnPhase.value='idle'; progressPaused.value=false; consent.value = false;
  }
  function message(cause: unknown): string {
    if (typeof cause === 'string') return cause;
    if (cause && typeof cause === 'object' && 'code' in cause && typeof cause.code === 'string') return cause.code;
    return cause instanceof Error ? cause.message : 'testing_request_failed';
  }
  function recordError(cause: unknown, fromRefresh = false): void {
    error.value = message(cause); errorFromRefresh = fromRefresh;
    errorRejected.value = !fromRefresh && cause instanceof TestingError && (cause.status === 429 || cause.code === 'rate_limited');
    errorRequestId.value = cause && typeof cause === 'object' && 'requestId' in cause
      ? diagnosticRequestId(cause.requestId) ?? '' : '';
  }
  function clearError(): void {
    error.value = ''; errorRequestId.value = ''; errorRejected.value = false; errorFromRefresh = false;
  }
  function clearPendingTextTurn(): void {
    pendingTextTurn = undefined;
    uncertainTurn.value = false;
  }
  function reconcilePendingTextTurn(detail: TestSessionDetail): void {
    const pending = pendingTextTurn;
    if (!pending || detail.session.id !== pending.sessionId || detail.conversation.id !== pending.conversationId) return;
    const saved = detail.conversation.turns.some(turn => turn.role === 'user'
      && turn.text === pending.input && !pending.priorTurnIds.has(turn.id));
    if (saved) {
      clearPendingTextTurn();
    } else if (detail.conversationActive !== true) {
      if (!text.value) text.value = pending.input;
      clearPendingTextTurn();
    }
  }
  async function run(action: () => Promise<void>): Promise<void> {
    if (busy.value || !permitted.value) return;
    const current = epoch; ++readEpoch; progressClient.invalidate(); busy.value = true; error.value = ''; notice.value = '';
    errorRequestId.value = ''; errorRejected.value = false; errorFromRefresh = false;
    try { await action(); }
    catch (cause) { if (current === epoch && !(cause instanceof DOMException && cause.name === 'AbortError')) recordError(cause); }
    finally {
      // Visibility/online observers can read while a command is in flight.
      // Fence those snapshots as well as polls that preceded the command.
      if (current === epoch) { ++readEpoch; busy.value = false; }
    }
  }
  function refresh(): Promise<void> {
    // External observers cannot read a pre-command snapshot and later join the
    // command's reconciliation. Commands request their own fresh reads below.
    return busy.value ? Promise.resolve() : refreshState();
  }
  function refreshAfterCommand(includeCollections = true): Promise<void> {
    ++readEpoch; progressClient.invalidate();
    return refreshState(includeCollections);
  }
  let progressReadPending: Promise<void> | undefined;
  function refreshProgress(): Promise<void> {
    const currentDetail = selected.value;
    if (progressReadPending || progressPaused.value || !busy.value || !currentDetail
      || !['waiting', 'finalizing'].includes(turnPhase.value)) return Promise.resolve();
    const currentEpoch = epoch; const currentRead = readEpoch;
    const sessionId = currentDetail.session.id; const conversationId = currentDetail.conversation.id;
    let task!: Promise<void>;
    task = (async () => {
      try {
        const detail = await progressClient.session(sessionId);
        if (disposed || currentEpoch !== epoch || currentRead !== readEpoch || !busy.value
          || turnPhase.value === 'recovering' || selected.value?.session.id !== sessionId
          || selected.value.conversation.id !== conversationId || detail.session.id !== sessionId
          || detail.conversation.id !== conversationId) return;
        selected.value = { ...selected.value,
          conversationActive: detail.conversationActive,
          conversationOperation: detail.conversationOperation,
        };
      } catch {
        // Progress reads are advisory. The command's result and normal detail
        // reconciliation remain authoritative and own user-visible failures.
      } finally {
        if (progressReadPending === task) progressReadPending = undefined;
      }
    })();
    progressReadPending = task;
    return task;
  }
  function refreshState(includeCollections = true): Promise<void> {
    if (!permitted.value || disposed) return Promise.resolve();
    if (client.cooldownRemainingMs() > 0) return Promise.resolve();
    if (refreshPending?.epoch === epoch) {
      const coversRequest = refreshPending.collections || !includeCollections;
      return refreshPending.request === readEpoch && coversRequest
        ? refreshPending.promise
        : refreshPending.promise.then(() => refreshState(includeCollections));
    }
    const current = epoch; const request = ++readEpoch; const id = selected.value?.session.id;
    if (includeCollections) nextCollectionRefreshAt = Date.now() + 30000;
    const pending = { epoch: current, request, collections: includeCollections, promise: Promise.resolve() };
    pending.promise = (async () => {
      const [list, people, detail] = await Promise.allSettled([
        includeCollections ? client.sessions() : Promise.resolve(sessions.value),
        includeCollections ? client.characters() : Promise.resolve(characters.value),
        id ? client.session(id) : Promise.resolve(null),
      ]);
      if (epoch !== current || request !== readEpoch) return;
      if (list.status === 'fulfilled') sessions.value = list.value;
      if (people.status === 'fulfilled') characters.value = people.value;
      if (detail.status === 'fulfilled' && detail.value && selected.value?.session.id === id) {
        selected.value = detail.value;
        reconcilePendingTextTurn(detail.value);
        if (detail.value.conversationActive !== true && turnPhase.value === 'recovering') turnPhase.value = 'idle';
      }
      const detailFailure = detail.status === 'rejected' ? detail : undefined;
      const failure = detailFailure ?? [people, list].find(result => result.status === 'rejected');
      // Collection failures must remain visible, but they do not make the
      // selected conversation stale when its authoritative detail succeeded.
      stale.value = !!detailFailure;
      if (failure?.status === 'rejected') {
        if (!(failure.reason instanceof DOMException && failure.reason.name === 'AbortError') && (!error.value || errorFromRefresh)) recordError(failure.reason, true);
      } else if (errorFromRefresh) {
        error.value = ''; errorRequestId.value = ''; errorRejected.value = false; errorFromRefresh = false;
      }
    })().finally(() => { if (refreshPending === pending) refreshPending = undefined; });
    refreshPending = pending;
    return pending.promise;
  }
  async function resume(id: string): Promise<void> {
    await run(async () => {
      client.invalidate(); stopVoice(); const current = epoch; const request = ++readEpoch; const detail = await client.session(id);
      if (current !== epoch || request !== readEpoch) return;
      selected.value = detail; inputMode.value = detail.session.inputMode;
      text.value = ''; stale.value = false;
      if (pendingTextTurn && (pendingTextTurn.sessionId !== detail.session.id
        || pendingTextTurn.conversationId !== detail.conversation.id)) clearPendingTextTurn();
      reconcilePendingTextTurn(detail);
      if (turnPhase.value === 'recovering' && detail.conversationActive !== true) turnPhase.value = 'idle';
      await router.replace({ path: '/testing-sessions', query: { session: id } });
    });
  }
  async function start(): Promise<void> {
    if (!consent.value) { recordError('Potvrďte souhlas se skutečným zpracováním OpenAI a uložením testu.'); return; }
    await run(async () => {
      operationId ??= crypto.randomUUID();
      const session = await client.create('gpt-live-1', inputMode.value, consent.value, operationId);
      operationId = null; stopVoice(); selected.value = await client.session(session.id);
      if (pendingTextTurn && (pendingTextTurn.sessionId !== selected.value.session.id
        || pendingTextTurn.conversationId !== selected.value.conversation.id)) clearPendingTextTurn();
      text.value = '';
      await router.replace({ path: '/testing-sessions', query: { session: session.id } });
      await refreshAfterCommand();
    });
  }
  async function saveCharacter(userInput: CharacterInput, character?: TestCharacter, longTermConsent = false): Promise<boolean> {
    if (!consent.value) { recordError('Pro odeslání postavy OpenAI potvrďte souhlas.'); return false; }
    let savedSuccessfully = false;
    await run(async () => {
      const saved = character ? await client.editCharacter(character.id, userInput, character.profile.revision, true, longTermConsent)
        : await client.createCharacter(userInput, true, longTermConsent);
      savedSuccessfully = true;
      characters.value = [...characters.value.filter(item => item.id !== saved.id), saved];
      const started=(selected.value?.conversation.turns.length??0)>0;
      notice.value = started ? 'Postava uložena. Probíhající rozhovor zachovává původní kontext; novou verzi použijte v novém testovacím sezení.' : 'Postava uložena. AI dohledání se zpracuje na pozadí.';
      if (selected.value && open.value && !started) selected.value = await client.select(selected.value.session.id, saved.id, selected.value.session.revision);
      await refreshAfterCommand();
    });
    return savedSuccessfully;
  }
  async function labelCharacter(character: TestCharacter, label: string): Promise<boolean> {
    let savedSuccessfully = false;
    await run(async () => {
      await client.labelCharacter(character.id, label.trim(), character.label ?? null);
      savedSuccessfully = true;
      await refreshAfterCommand();
    });
    return savedSuccessfully;
  }
  async function selectCharacter(characterId: string): Promise<void> {
    if (!selected.value) return;
    await run(async () => {
      stopVoice(); selected.value = await client.select(selected.value!.session.id, characterId, selected.value!.session.revision);
      await refreshAfterCommand();
    });
  }
  async function enrich(): Promise<void> {
    if (!selected.value?.character || !consent.value) return;
    await run(async () => {
      const id=selected.value!.session.id;const characterId=selected.value!.character!.id;
      await client.enrich(characterId);
      if(selected.value!.conversation.turns.length===0){selected.value=await client.select(id,characterId,selected.value!.session.revision);}
      else notice.value='AI obohacení je uložené pro nové testovací sezení. Probíhající rozhovor zachovává původní kontext.';
      await refreshAfterCommand();
    });
  }
  async function awaitRetired(id:string):Promise<void> {
    const initialEpoch=epoch;const deadline=performance.now()+10000;let delayMs=250;
    for(;;){
      if(epoch!==initialEpoch || selected.value?.session.id!==id)throw new DOMException('Testing session changed.','AbortError');
      const detail=await client.session(id);
      if(epoch!==initialEpoch || selected.value?.session.id!==id)throw new DOMException('Testing session changed.','AbortError');
      if(detail.conversationActive!==true){selected.value=detail;if(retiringSessionId===id)retiringSessionId=null;return;}
      const remaining=deadline-performance.now();
      if(remaining<=0)throw new Error('Předchozí poskytovatelské spojení se ještě uzavírá. Obnovte stav a zkuste zprávu znovu.');
      await new Promise<void>(resolve=>setTimeout(resolve,Math.min(delayMs,remaining)));
      delayMs=Math.min(1000,Math.round(delayMs*1.6));
    }
  }
  async function send(): Promise<void> {
    if (!selected.value || selected.value.session.engine !== 'gpt-live-1' || !text.value.trim() || !open.value || !consent.value || spokenBusy.value || busy.value || stale.value || uncertainTurn.value) return;
    if (!voiceActive.value && selected.value.conversationActive) {
      recordError('Předchozí požadavek ještě probíhá. Vyčkejte a obnovte stav.');
      return;
    }
    const input = text.value.trim();
    if (outputEnabled.value) {
      if (voiceActive.value && !captureEnabled.value) {
        if (voice.sendText(input)) {text.value = '';spokenBusy.value = true;} else recordError('Výstupní spojení zatím není připraveno.');
      } else await startSpokenText(input);
      return;
    }
    const wasConnected=voiceActive.value || retiringSessionId===selected.value?.session.id;
    if(wasConnected)stopVoice();
    await run(async () => {
      if(wasConnected)await awaitRetired(selected.value!.session.id);
      const id = selected.value!.conversation.id;
      const priorTurnIds = new Set(selected.value!.conversation.turns.map(turn => turn.id));
      const sessionId = selected.value!.session.id;
      turnPhase.value = 'waiting';
      try {
        text.value = '';
        updateConversation(await lab.text(id, input));
        turnPhase.value = 'finalizing';
        progressPaused.value = true;
        try { await refreshAfterCommand(false); }
        finally { progressPaused.value = false; }
        turnPhase.value = 'idle';
      } catch (cause) {
        // A gateway/provider failure may follow a saved turn. Reconcile before
        // retaining the draft so a manual retry cannot silently duplicate it.
        turnPhase.value = 'recovering';
        pendingTextTurn = { sessionId, conversationId: id, input, priorTurnIds };
        uncertainTurn.value = true;
        try {
          progressPaused.value = true;
          await refreshAfterCommand(false);
        } catch {
          // Preserve the original provider/request error; the next explicit
          // status refresh remains available from the error notice.
        } finally {
          progressPaused.value = false;
        }
        throw cause;
      }
    });
  }
  async function startVoice(): Promise<void> {
    if (!selected.value || selected.value.session.engine !== 'gpt-live-1' || !open.value || !consent.value || inputMode.value === 'text' || busy.value || stale.value || uncertainTurn.value) return;
    const hadConnection=voiceActive.value || retiringSessionId===selected.value.session.id;stopVoice();
    if(hadConnection){try{await awaitRetired(selected.value.session.id);}catch(cause){recordError(cause);return;}}
    outputEnabled.value = true;
    captureEnabled.value = true; voice = makeVoice(); voice.setOutputEnabled(true); clearError();
    const id = selected.value.conversation.id;
    await voice.connect(() => lab.ticket(id, voicePhase.value === 'error'), id, accountOrigin.value || window.location.origin);
  }
  async function startSpokenText(input = ''): Promise<void> {
    if (!selected.value || selected.value.session.engine !== 'gpt-live-1' || !open.value || !consent.value || busy.value) return;
    const hadConnection=voiceActive.value || retiringSessionId===selected.value.session.id;stopVoice();
    if(hadConnection){try{await awaitRetired(selected.value.session.id);}catch(cause){recordError(cause);return;}}
    pendingSpokenText = input; spokenBusy.value = input.length > 0; captureEnabled.value = false; voice = makeVoice();
    voice.setOutputEnabled(true); clearError();
    const id = selected.value.conversation.id;
    await voice.connect(() => lab.ticket(id,false,true), id, accountOrigin.value || window.location.origin, {capture:false});
  }
  function changeMode(mode: InputMode): void {
    const wasConnected=voiceActive.value || retiringSessionId===selected.value?.session.id;
    if (wasConnected) stopVoice();
    if (!selected.value || !open.value) { inputMode.value = mode; return; }
    void run(async () => { if(wasConnected)await awaitRetired(selected.value!.session.id); await client.mode(selected.value!.session.id, mode, selected.value!.session.revision); selected.value = await client.session(selected.value!.session.id); inputMode.value = selected.value.session.inputMode; });
  }
  async function selectWand(): Promise<void> {
    if (!selected.value || !consent.value) return;
    await run(async () => { selected.value = await client.selectWand(selected.value!.session.id); });
  }
  async function generate(): Promise<void> {
    if (!selected.value || !consent.value) return;
    await run(async () => { selected.value = await client.generate(selected.value!.session.id); });
  }
  async function fault(value: string): Promise<void> {
    if (!selected.value) return;
    await run(async () => { selected.value = await client.fault(selected.value!.session.id, value); });
  }
  async function end(): Promise<void> {
    if (!selected.value) return;
    const id = selected.value.session.id; stopVoice(); client.invalidate(); lab.invalidate();
    await run(async () => { await client.end(id); selected.value = await client.session(id); await refreshAfterCommand(); });
  }
  async function cancel(): Promise<void> {
    if (!selected.value) return;
    const id = selected.value.session.id; stopVoice(); lab.invalidate(); client.invalidate(); clearError();
    try { await client.cancel(id); await refreshAfterCommand(); }
    catch (cause) { if (!(cause instanceof DOMException && cause.name === 'AbortError')) recordError(cause); }
  }
  async function remove(): Promise<void> {
    if (!selected.value) return;
    const id = selected.value.session.id; stopVoice();
    await run(async () => {
      await client.delete(id); selected.value = null; text.value = ''; clearPendingTextTurn(); await router.replace('/testing-sessions'); await refreshAfterCommand();
    });
  }
  async function load(): Promise<void> {
    if (!permitted.value) return;
    loading.value = true; await refresh(); loading.value = false;
    const id = route.query.session;
    if (typeof id === 'string') await resume(id);
  }
  watch(identity, () => { invalidate(true); void load(); }, { immediate: true });
  watch(outputEnabled, value => voice.setOutputEnabled(value));
  const refreshTimer = setInterval(() => {
    if (document.hidden || busy.value || client.cooldownRemainingMs() > 0) return;
    const recoveringConversation = selected.value?.conversationActive === true;
    if (recoveringConversation) return;
    const includeCollections = !recoveringConversation && Date.now() >= nextCollectionRefreshAt;
    void refreshState(includeCollections);
  }, 10000);
  const activeOperationTimer = setInterval(() => {
    if (document.hidden || busy.value || selected.value?.conversationActive !== true || client.cooldownRemainingMs() > 0) return;
    void refreshState(false);
  }, 2000);
  const progressTimer = setInterval(() => {
    if (document.hidden || progressPaused.value || !busy.value || !['waiting', 'finalizing'].includes(turnPhase.value)) return;
    void refreshProgress();
  }, 2000);
  const clockTimer = setInterval(() => { clock.value = Date.now(); }, 1000);
  const hidden = () => { if (document.hidden) stopVoice(); else void refresh(); };
  const online = () => void refresh();
  document.addEventListener('visibilitychange', hidden); window.addEventListener('online', online);
  onBeforeUnmount(() => { disposed = true; invalidate(); clearInterval(refreshTimer); clearInterval(activeOperationTimer); clearInterval(progressTimer); clearInterval(clockTimer); document.removeEventListener('visibilitychange', hidden); window.removeEventListener('online', online); });
  const retrySeconds = computed(() => Math.ceil(Math.max(0, client.cooldownUntil() - clock.value) / 1000));
  return { account, permitted, sessions, characters, selected, error, errorRequestId, errorRejected, retrySeconds, notice, busy, loading, stale, inputMode, consent,
    text, outputEnabled, captureEnabled, spokenBusy, providerConfirmed, voicePhase, voiceActive, partial, turnPhase, uncertainTurn, open, client, run, refresh, refreshAfterCommand, resume, start, saveCharacter, labelCharacter, selectCharacter,
    enrich, send, selectWand, generate, fault, startVoice, startSpokenText, stopVoice, changeMode, end, cancel, remove, interrupt: () => voice.interrupt() };
}
