import { computed, onBeforeUnmount, ref } from 'vue';
import { account } from '../identity/session';
import { GameError, operation } from '../game/game';
import { finishSparkRun, issueSparkTicket, readSparkStandings, type SparkReceipt, type SparkReplay, type SparkStandings, type SparkTicket } from './sparkApi';

interface PendingRun { ticket: SparkTicket; replay: SparkReplay }

/** Delivery is independent of the running game; interrupted requests remain retryable. */
export function useSparkCompetition() {
  const standings = ref<SparkStandings>();
  const ticket = ref<SparkTicket>();
  const preparing = ref(false);
  const error = ref('');
  const saving = ref(false);
  const pending = ref<PendingRun[]>([]);
  const receipt = ref<{ runId: string; value: SparkReceipt }>();
  const lifetime = new AbortController();
  let disposed = false;
  let issuanceId = operation();
  let standingsGeneration = 0;
  const owner = account.value?.id;
  const queueKey = `wns.spark.pending.v1.${owner ?? 'anonymous'}`;
  const best = computed(() => standings.value?.best ?? 0);
  const next = computed(() => standings.value?.ranking.filter(row => row.score > best.value).at(-1));

  function persist() {
    try { localStorage.setItem(queueKey, JSON.stringify(pending.value)); } catch { /* In-memory retry remains available. */ }
  }
  function restore() {
    if (!owner) return;
    try {
      const stored: unknown = JSON.parse(localStorage.getItem(queueKey) || '[]');
      if (Array.isArray(stored)) pending.value = stored.filter((entry): entry is PendingRun =>
        typeof entry?.ticket?.runId === 'string' && Number.isFinite(entry.ticket.expiresAt)
        && entry.ticket.expiresAt > Date.now() && Number.isInteger(entry?.replay?.endTick)
        && Array.isArray(entry.replay.taps) && entry.replay.taps.length <= 24000
        && entry.replay.taps.every(Number.isInteger));
    } catch { /* Corrupt or unavailable local storage cannot prevent play. */ }
  }
  async function request<T>(work: (signal: AbortSignal) => Promise<T>): Promise<T> {
    const controller = new AbortController();
    const abort = () => controller.abort();
    lifetime.signal.addEventListener('abort', abort, { once: true });
    const timeout = window.setTimeout(abort, 9000);
    try { return await work(controller.signal); }
    finally { clearTimeout(timeout); lifetime.signal.removeEventListener('abort', abort); }
  }
  async function refresh() {
    const generation = ++standingsGeneration;
    try {
      const current = await request(readSparkStandings);
      if (!disposed && generation === standingsGeneration) standings.value = current;
    } catch { if (!disposed) error.value = 'Žebříček se nepodařilo načíst. Trénink může pokračovat.'; }
  }
  async function prepare() {
    if (preparing.value || disposed || pending.value.length >= 5 || ticket.value) return;
    preparing.value = true;
    try {
      const issued = await request(signal => issueSparkTicket(signal, issuanceId));
      if (!disposed) { ticket.value = issued; issuanceId = operation(); error.value = ''; }
    } catch { if (!disposed) error.value = 'Soutěžní pokus není připravený. Můžeš trénovat nebo zkusit spojení znovu.'; }
    finally { preparing.value = false; }
  }
  function takeTicket(): SparkTicket | undefined {
    if (pending.value.length >= 5) return;
    const current = ticket.value;
    ticket.value = undefined;
    if (!current || current.expiresAt <= Date.now()) { void prepare(); return; }
    void prepare();
    return current;
  }
  async function retry() {
    if (saving.value || disposed) return;
    saving.value = true;
    try {
      while (pending.value.length) {
        const run = pending.value[0]!;
        try {
          const value = await request(signal => finishSparkRun(run.ticket.runId, run.replay, signal));
          if (disposed) return;
          receipt.value = { runId: run.ticket.runId, value };
          standingsGeneration++;
          if (standings.value?.rulesVersion === run.ticket.rulesVersion) {
            standings.value.best = Math.max(standings.value.best, value.best);
          }
          pending.value = pending.value.filter(item => item.ticket.runId !== run.ticket.runId);
          persist(); error.value = '';
        } catch (problem) {
          if (disposed) return;
          if (problem instanceof GameError && [400, 403, 404, 409, 410, 413].includes(problem.status)) {
            pending.value = pending.value.filter(item => item.ticket.runId !== run.ticket.runId);
            persist();
            error.value = 'Výsledek už nelze ověřit a do žebříčku se nezapsal. Zahraj nový soutěžní pokus.';
          } else error.value = 'Výsledek čeká v tomto prohlížeči na odeslání. Další pokus můžeš hrát hned.';
          break;
        }
      }
    } finally { saving.value = false; }
    if (!disposed) { void refresh(); void prepare(); }
  }
  function submit(issued: SparkTicket, replay: SparkReplay) {
    pending.value.push({ ticket: issued, replay });
    persist();
    void retry();
  }
  async function connect() { await Promise.all([refresh(), prepare()]); }
  restore();
  onBeforeUnmount(() => { disposed = true; lifetime.abort(); });
  const availableTicket = computed(() => pending.value.length < 5 ? ticket.value : undefined);
  return { standings, ticket: availableTicket, preparing, error, saving, pending, receipt, best, next, refresh, prepare, takeTicket, submit, retry, connect };
}
