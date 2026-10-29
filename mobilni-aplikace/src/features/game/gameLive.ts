import { Capacitor } from '@capacitor/core';
import { AbortError, HttpClient, HttpResponse, HttpTransportType, HubConnectionBuilder, LogLevel, TimeoutError, type HttpRequest } from '@microsoft/signalr';
import { accountTransport } from '../identity/session';

const hubPath = '/app/v1/game/live';

// Keep SignalR negotiation and long polling in the same authenticated transport
// as game commands, including the native apps' private cookie jar and CSRF.
export class GameLiveHttpClient extends HttpClient {
  async send(request: HttpRequest): Promise<HttpResponse> {
    if (request.abortSignal?.aborted) throw new AbortError();
    const base = new URL(window.location.href);
    const url = new URL(request.url ?? '', base);
    if (url.protocol !== base.protocol || url.host !== base.host || url.username || url.password || url.hash
      || ![hubPath, `${hubPath}/negotiate`].includes(url.pathname)) {
      throw new Error('Invalid game live URL.');
    }
    const controller = new AbortController();
    let failure: Error | undefined;
    if (request.abortSignal) request.abortSignal.onabort = () => {
      failure = new AbortError();
      controller.abort();
    };
    const timeout = request.timeout ? setTimeout(() => {
      failure = new TimeoutError();
      controller.abort();
    }, request.timeout) : undefined;
    const headers = new Headers(request.headers);
    headers.delete('X-SignalR-User-Agent'); // Optional diagnostics, outside the native header allowlist.
    if (request.content) headers.set('Content-Type', typeof request.content === 'string' ? 'text/plain;charset=UTF-8' : 'application/octet-stream');
    try {
      const response = await accountTransport(`${url.pathname}${url.search}`, {
        method: request.method, headers, body: request.content || undefined, signal: controller.signal,
      });
      const content = request.responseType === 'arraybuffer' ? await response.arrayBuffer() : await response.text();
      return new HttpResponse(response.status, response.statusText, content);
    } catch (cause) {
      throw failure ?? cause;
    } finally {
      clearTimeout(timeout);
      if (request.abortSignal) request.abortSignal.onabort = null;
    }
  }
}

export function connectGameLive(changed: () => void): () => void {
  const connection = new HubConnectionBuilder()
    .withUrl(hubPath, {
      httpClient: new GameLiveHttpClient(),
      // WebViews cannot use the native cookie jar from JavaScript WebSockets.
      transport: Capacitor.isNativePlatform() ? HttpTransportType.LongPolling : HttpTransportType.WebSockets | HttpTransportType.LongPolling,
    })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: context => Math.min(30000, 1000 * 2 ** Math.min(context.previousRetryCount, 5)) })
    .configureLogging(LogLevel.None)
    .build();
  let stopped = false;
  let retry: ReturnType<typeof setTimeout> | undefined;
  function invalidate() { if (!stopped) changed(); }
  async function start() {
    try {
      await connection.start();
      invalidate(); // Recover changes between the initial REST read and joining.
    } catch {
      if (!stopped) retry = setTimeout(() => { void start(); }, 30000);
    }
  }
  connection.on('gameChanged', invalidate);
  connection.onreconnected(invalidate);
  void start();
  return () => {
    stopped = true;
    clearTimeout(retry);
    connection.off('gameChanged', invalidate);
    void connection.stop().catch(() => {});
  };
}
