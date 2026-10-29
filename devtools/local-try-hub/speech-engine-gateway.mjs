// A tunnel must never expose the development app or its local login capability.
// This loopback gateway forwards only a signed Speech Engine WebSocket upgrade.
import http from 'node:http';
import { pathToFileURL } from 'node:url';

export function createSpeechEngineGateway({ backendPort = 5082 } = {}) {
const sockets = new Set();
const reject = (socket, status) => socket.end(`HTTP/1.1 ${status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
const server = http.createServer({ maxHeaderSize: 16384 }, (request, response) => {
  response.writeHead(request.url === '/healthz' ? 200 : 404, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
  response.end(request.url === '/healthz' ? 'ready' : '');
});
server.maxConnections = 64;
server.on('connection', socket => {
  sockets.add(socket);
  socket.on('close', () => sockets.delete(socket));
});
server.on('upgrade', (request, client, head) => {
  if (request.method !== 'GET' || !/^\/app\/v1\/customer\/wand\/speech-engine\/[0-9a-f]{64}$/.test(request.url ?? '')
    || typeof request.headers['x-elevenlabs-speech-engine-authorization'] !== 'string'
    || request.headers['x-elevenlabs-speech-engine-authorization'].length > 8192
    || typeof request.headers['sec-websocket-key'] !== 'string' || request.headers['sec-websocket-version'] !== '13') {
    reject(client, '404 Not Found'); return;
  }
  const upstream = http.request({
    host: '127.0.0.1', port: backendPort, method: 'GET', path: request.url, timeout: 10000,
    headers: {
      host: 'localhost:5082', connection: 'Upgrade', upgrade: 'websocket',
      'sec-websocket-key': request.headers['sec-websocket-key'], 'sec-websocket-version': '13',
      'x-elevenlabs-speech-engine-authorization': request.headers['x-elevenlabs-speech-engine-authorization'],
      'x-forwarded-proto': 'https',
    },
  });
  client.on('error', () => upstream.destroy());
  upstream.on('timeout', () => upstream.destroy());
  upstream.on('error', () => { if (!client.destroyed) reject(client, '502 Bad Gateway'); });
  upstream.on('response', response => { response.resume(); reject(client, `${response.statusCode ?? 502} Rejected`); });
  upstream.on('upgrade', (response, backend, backendHead) => {
    upstream.setTimeout(0);
    const headers = ['HTTP/1.1 101 Switching Protocols'];
    for (const name of ['upgrade', 'connection', 'sec-websocket-accept', 'sec-websocket-protocol', 'sec-websocket-extensions']) {
      if (typeof response.headers[name] === 'string') headers.push(`${name}: ${response.headers[name]}`);
    }
    client.write(headers.join('\r\n') + '\r\n\r\n');
    if (backendHead.length) client.write(backendHead);
    if (head.length) backend.write(head);
    const deadline = setTimeout(() => { client.destroy(); backend.destroy(); }, 215000);
    client.on('close', () => { clearTimeout(deadline); backend.destroy(); });
    backend.on('close', () => { clearTimeout(deadline); client.destroy(); });
    backend.on('error', () => client.destroy());
    client.on('error', () => backend.destroy());
    client.pipe(backend); backend.pipe(client);
  });
  upstream.end();
});
return { server, close: () => { for (const socket of sockets) socket.destroy(); server.close(); } };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const gateway = createSpeechEngineGateway();
  gateway.server.listen(5083, '127.0.0.1');
  process.on('SIGINT', gateway.close);
  process.on('SIGTERM', gateway.close);
}
