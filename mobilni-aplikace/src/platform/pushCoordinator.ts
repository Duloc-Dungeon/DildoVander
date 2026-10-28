let mutationTail: Promise<unknown> = Promise.resolve();
let reconnectAllowed = true;
let reconnectRevision = 0;

// A revocation must run after any already-admitted token update. Older callbacks
// must then remain blocked, including callbacks arriving while DELETE is pending.
export function serializePushMutation<T>(action: () => Promise<T>): Promise<T> {
  const result = mutationTail.then(action, action);
  mutationTail = result.catch(() => {});
  return result;
}

export function pausePushReconnection(): void {
  reconnectAllowed = false;
  ++reconnectRevision;
}

export function resumePushReconnection(): void {
  reconnectAllowed = true;
  ++reconnectRevision;
}

export function pushReconnectionCheck(): () => void {
  const revision = reconnectRevision;
  return () => {
    if (!reconnectAllowed || revision !== reconnectRevision) throw new Error('Obnova oznámení byla zrušena.');
  };
}
