/** Fresh per-request context only. No user/device identifier or stored state. */
export function createTraceparent(source = globalThis.crypto) {
  if (typeof source?.getRandomValues !== 'function') return undefined;
  try {
    const bytes = source.getRandomValues(new Uint8Array(24));
    const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
    const traceId = hex.slice(0, 32);
    const parentId = hex.slice(32);
    if (/^0+$/.test(traceId) || /^0+$/.test(parentId)) return undefined;
    // Sampling stays a server decision; the app does not export client spans.
    return `00-${traceId}-${parentId}-00`;
  } catch {
    // Diagnostics must not prevent an otherwise valid application request.
    return undefined;
  }
}

export function correlationHeaders(initial) {
  const headers = new Headers(initial);
  // Callers cannot retain an old trace or inject arbitrary propagation data.
  headers.delete('traceparent');
  headers.delete('tracestate');
  headers.delete('baggage');
  const parent = createTraceparent();
  if (parent) headers.set('traceparent', parent);
  return headers;
}

/** Only the application's relative backend API routes receive context. */
export function backendFetch(input, init = {}) {
  if (typeof input !== 'string' || !/^\/(?:app\/v1|guest\/v1|v1)\//.test(input)
      || /[\\\s#]/.test(input)) return fetch(input, init);
  return fetch(input, { ...init, headers: correlationHeaders(init.headers), redirect: 'error' });
}
