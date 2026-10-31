// A minimal obs-websocket v5 server for trying the web UI without a real OBS.
// Usage: node tests/ui/fake-obs.mjs [port]   (default 14455, no password)

import { createServer } from 'node:http';
import { createHash } from 'node:crypto';

const port = Number(process.argv[2] || 14455);
const state = {
  current: 'Main',
  scenes: ['Starting Soon', 'Main', 'Gameplay', 'Game Over', 'BRB', 'Ending'],
  items: {
    Main: [{ sceneItemId: 1, sourceName: 'Webcam', sceneItemEnabled: true }, { sceneItemId: 2, sourceName: 'Alerts', sceneItemEnabled: true }, { sceneItemId: 3, sourceName: 'Ticker', sceneItemEnabled: false }],
    Gameplay: [{ sceneItemId: 1, sourceName: 'Game Capture', sceneItemEnabled: true }, { sceneItemId: 2, sourceName: 'Webcam', sceneItemEnabled: true }],
  },
};

const frame = (text) => {
  const payload = Buffer.from(text);
  const len = payload.length;
  const head = len < 126 ? Buffer.from([0x81, len]) : len < 65536 ? Buffer.from([0x81, 126, len >> 8, len & 255]) : null;
  if (!head) { const h = Buffer.alloc(10); h[0] = 0x81; h[1] = 127; h.writeBigUInt64BE(BigInt(len), 2); return Buffer.concat([h, payload]); }
  return Buffer.concat([head, payload]);
};

function respond(type, data) {
  switch (type) {
    case 'GetVersion': return { obsVersion: '31.0.0', obsWebSocketVersion: '5.5.0' };
    case 'GetSceneList': return { currentProgramSceneName: state.current, scenes: state.scenes.map((s, i) => ({ sceneName: s, sceneIndex: state.scenes.length - 1 - i })) };
    case 'GetCurrentProgramScene': return { currentProgramSceneName: state.current, sceneName: state.current };
    case 'GetSceneItemList': return { sceneItems: [...(state.items[data?.sceneName] || [])].reverse() };
    case 'GetSourceFilterList': return { filters: [{ filterName: 'Blur', filterEnabled: false }, { filterName: 'Color Correction', filterEnabled: true }] };
    case 'GetInputList': return { inputs: ['Webcam', 'Alerts', 'Ticker', 'Game Capture', 'Mic'].map(n => ({ inputName: n, inputKind: 'fake' })) };
    case 'GetStats': return { cpuUsage: 3.2, activeFps: 60 };
    default: return {};
  }
}

const server = createServer();
server.on('upgrade', (req, socket) => {
  const accept = createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  const protocol = req.headers['sec-websocket-protocol'] ? 'Sec-WebSocket-Protocol: obswebsocket.json\r\n' : '';
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n${protocol}\r\n`);
  const send = (op, d) => socket.write(frame(JSON.stringify({ op, d })));
  send(0, { obsWebSocketVersion: '5.5.0', rpcVersion: 1 });
  let buf = Buffer.alloc(0);
  socket.on('data', (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    while (buf.length >= 2) {
      const opcode = buf[0] & 15;
      let len = buf[1] & 127, off = 2;
      if (len === 126) { len = buf.readUInt16BE(2); off = 4; } else if (len === 127) { len = Number(buf.readBigUInt64BE(2)); off = 10; }
      const masked = (buf[1] & 128) !== 0;
      if (buf.length < off + (masked ? 4 : 0) + len) return;
      const mask = masked ? buf.subarray(off, off + 4) : null;
      off += masked ? 4 : 0;
      const payload = Buffer.from(buf.subarray(off, off + len));
      if (mask) for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i % 4];
      buf = buf.subarray(off + len);
      if (opcode === 8) { socket.end(); return; }
      if (opcode === 9) { socket.write(Buffer.from([0x8a, 0])); continue; }
      if (opcode !== 1) continue;
      const msg = JSON.parse(payload.toString());
      if (msg.op === 1) send(2, { negotiatedRpcVersion: 1 });
      if (msg.op === 6) {
        const { requestType, requestId, requestData } = msg.d;
        if (requestType === 'SetCurrentProgramScene') {
          state.current = requestData.sceneName;
          send(5, { eventType: 'CurrentProgramSceneChanged', eventIntent: 4, eventData: { sceneName: state.current } });
        }
        if (requestType === 'SetSceneItemEnabled') {
          const item = (state.items[requestData.sceneName] || []).find(i => i.sceneItemId === requestData.sceneItemId);
          if (item) item.sceneItemEnabled = requestData.sceneItemEnabled;
        }
        console.log(requestType, JSON.stringify(requestData || {}));
        send(7, { requestType, requestId, requestStatus: { result: true, code: 100 }, responseData: respond(requestType, requestData) });
      }
    }
  });
  socket.on('error', () => {});
});
server.listen(port, '127.0.0.1', () => console.log(`fake OBS listening on ws://127.0.0.1:${port}`));
