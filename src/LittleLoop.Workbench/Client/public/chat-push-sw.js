self.addEventListener('push', event => {
  event.waitUntil((async () => {
    let data;
    try { data = event.data.json(); } catch { return; }
    if (!/^[0-9a-f-]{36}$/i.test(data.room) || typeof data.title !== 'string' || typeof data.body !== 'string') return;
    await self.registration.showNotification(data.title.slice(0, 100), {
      body: data.body.slice(0, 160), tag: 'chat-' + data.room, data: { room: data.room, sequence: data.sequence },
    });
  })());
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const room = event.notification.data?.room;
  if (!/^[0-9a-f-]{36}$/i.test(room)) return;
  event.waitUntil((async () => {
    const target = new URL('/chat', self.location.origin);
    target.searchParams.set('room', room);
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
      if (new URL(client.url).pathname !== '/chat') continue;
      if (await client.navigate(target.href)) { await client.focus(); return; }
    }
    await self.clients.openWindow(target.href);
  })());
});
