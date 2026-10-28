/* Push-only worker: no fetch handler and no offline cache of account content. */
const allowedPath = value => typeof value === 'string' && (
  ['/play', '/launch', '/me', '/me/account'].includes(value)
  || /^\/play\/match\/[A-Za-z0-9-]{1,80}$/.test(value)
) ? value : '/me/account';

self.addEventListener('push', event => {
  let payload = {};
  try { payload = event.data?.json() ?? {}; } catch { /* Show a safe fallback. */ }
  event.waitUntil(self.registration.showNotification('Wand Night Stand', {
    body: typeof payload.body === 'string' ? payload.body.slice(0, 240) : 'V aplikaci na tebe čeká nové oznámení.',
    icon: '/push-icon.png',
    tag: typeof payload.tag === 'string' ? payload.tag.slice(0, 120) : 'wand-night-stand',
    data: { url: allowedPath(payload.url) },
  }));
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const destination = new URL(allowedPath(event.notification.data?.url), self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
      if (new URL(client.url).origin === self.location.origin && 'navigate' in client) {
        const navigated = await client.navigate(destination);
        if (navigated) { await navigated.focus(); return; }
      }
    }
    await self.clients.openWindow(destination);
  })());
});
