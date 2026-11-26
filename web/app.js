'use strict';

// ============================================================
//  painter Web 远控客户端
//  · 通过 WebSocket(/ws) 接收被控端画面（由 WebServer 扇出，支持多浏览器并发）
//  · 多鼠标：每个浏览器 = 一个独立"虚拟光标"，WebServer 把各光标位置 + 操作者 IP 广播给所有人
//  · 鼠标 / 触摸统一为"绝对坐标"映射到被控端屏幕
//  · 触屏手势：轻触=移动+左键单击；单指拖动=按住左键拖动；长按=右键；双指上下滑=滚轮
//  登录与心跳由 WebServer 代劳，浏览器只管渲染与输入。
// ============================================================

const wsProto = location.protocol === 'https:' ? 'wss://' : 'ws://';

// Windows 7 的 HTTP.sys 不支持 WebSocket：服务端会在另一个 TCP 端口上开兼容通道，
// 端口号由 /wsconfig 告知（0 表示与页面同源，直接走 location.host）。
let wsConfig = null;
let wsConfigPromise = null;

function loadWsConfig(force) {
  // force=true 用于每次连接/重连：Win7 兼容通道可能稍后才启动成功，
  // 永久缓存 wsPort=0 的旧结果会让画面一直连不上。
  if (force || !wsConfigPromise) {
    wsConfigPromise = fetch('/wsconfig', { cache: 'no-store' })
      .then(r => (r.ok ? r.json() : null))
      .catch(() => null)
      .then(cfg => { if (!cfg) wsConfigPromise = null; return cfg; });
  }
  return wsConfigPromise;
}

function wsHost() {
  let h = location.hostname;
  if (h.indexOf(':') >= 0 && h.charAt(0) !== '[') h = '[' + h + ']';
  const p = (wsConfig && wsConfig.wsPort) ? wsConfig.wsPort : location.port;
  return p ? h + ':' + p : h;
}

let ws = null;
let canvas, ctx2d, dpr = 1;
let kbd = null;
let screenReady = false;
let connected = false;
let authed = false;          // 是否已通过访问密码（服务器放行画面后才为 true）

// 视频层（离屏，仅存纯画面，避免同伴光标残影）
let videoCanvas = null, vctx = null;
let vdx = 0, vdy = 0, vdw = 0, vdh = 0;     // 画面在显示画布上的缩放位置
let imgW = 0, imgH = 0;

// 远端真实光标（S端）
let cursor = { visible: false, x: 0, y: 0, hotX: 0, hotY: 0, w: 0, h: 0, bmp: null };

// 多鼠标：同伴光标（含自己） id -> {ip,x,y}
let peers = new Map();
let myId = 0;
let renderPending = false;

// 指针 / 触摸状态
const pointers = new Map();
let leftDown = false;
let longPressTimer = null;
let wheelActive = false;
let wheelRefY = 0;

const BTN_LEFT = 1, BTN_RIGHT = 2, BTN_MIDDLE = 3;

// ---------- 初始化 ----------
function init() {
  canvas = document.getElementById('screen');
  ctx2d = canvas.getContext('2d', { alpha: false });
  dpr = window.devicePixelRatio || 1;
  videoCanvas = document.createElement('canvas');
  vctx = videoCanvas.getContext('2d');
  resizeCanvas();
  window.addEventListener('resize', resizeCanvas);

  canvas.addEventListener('pointerdown', onPointerDown);
  canvas.addEventListener('pointermove', onPointerMove);
  canvas.addEventListener('pointerup', onPointerUp);
  canvas.addEventListener('pointercancel', onPointerUp);
  canvas.addEventListener('contextmenu', e => e.preventDefault());
  canvas.addEventListener('wheel', onWheel, { passive: false });
  window.addEventListener('keydown', onKeyDown, true);
  window.addEventListener('keyup', onKeyUp, true);
  window.addEventListener('blur', () => releaseAll());

  // 隐藏输入框：捕获 IME/中文输入与移动端软键盘，input 事件把文本走 UnicodeText 通道。
  kbd = document.getElementById('kbd');
  if (kbd) {
    kbd.addEventListener('input', onKbdInput);
    kbd.addEventListener('keydown', e => { e.stopPropagation(); });  // 键盘事件由 window 捕获统一处理
  }

  const lb = document.getElementById('loginBtn');
  if (lb) lb.addEventListener('click', sendAuth);
  const pi = document.getElementById('pwd');
  if (pi) pi.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); sendAuth(); } });

  // 画质档位与控制动作按钮
  const qs = document.getElementById('quality');
  if (qs) {
    try { const saved = localStorage.getItem('painterQuality'); if (saved && QUALITY_PRESETS[saved]) qs.value = saved; } catch (_) {}
    qs.addEventListener('change', applyQuality);
  }
  const cad = document.getElementById('btnCad');
  if (cad) cad.addEventListener('click', () => sendControlAction(1));
  const blk = document.getElementById('btnLock');
  if (blk) blk.addEventListener('click', () => sendControlAction(2));
  const clipSend = document.getElementById('btnClipSend');
  if (clipSend) clipSend.addEventListener('click', () => {
    const box = document.getElementById('clipText');
    if (box && box.value) sendClipboardText(box.value);
  });
  const clipGet = document.getElementById('btnClipGet');
  if (clipGet) clipGet.addEventListener('click', () => sendControlAction(6));

  connect();
}

function resizeCanvas() {
  const wrap = document.getElementById('wrap');
  const w = wrap.clientWidth, h = wrap.clientHeight;
  canvas.style.width = w + 'px';
  canvas.style.height = h + 'px';
  canvas.width = Math.round(w * dpr);
  canvas.height = Math.round(h * dpr);
  if (screenReady) renderAll();
}

// ---------- WebSocket ----------
let connecting = false;
let reconnectTimer = null;
function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(); }, 800);
}

async function connect() {
  if (connecting) return;          // 多个定时器/事件可能并发触发，只允许一个连接在途
  connecting = true;
  setStatus('连接中…', 'warn');
  try {
    wsConfig = await loadWsConfig(true);
    if (!wsConfig) {
      setStatus('连接断开，正在重连…', 'warn');
      scheduleReconnect();
      return;
    }
    if (wsConfig.needCompat && !wsConfig.compat && wsConfig.error) {
      setStatus('兼容通道未就绪：' + wsConfig.error + '，重试中…', 'warn');
    }
    // 绑定本次连接的局部引用：await loadWsConfig 期间全局 ws 可能已被新连接替换，
    // 旧连接的 onerror/onclose 若引用全局 ws，会误关掉新连接并引发反复自断重连。
    const sock = new WebSocket(wsProto + wsHost() + '/ws');
    ws = sock;
    sock.binaryType = 'arraybuffer';
    sock.onopen = () => {
      if (sock !== ws) return;
      connected = true;
      setStatus('已连接', 'ok');
      // 画面/质量请求推迟到鉴权通过后再发（handleAuthResult 内）
    };
    sock.onclose = () => {
      if (sock !== ws) return;     // 旧连接的延迟事件，不再是当前连接
      connected = false;
      authed = false;
      screenReady = false;
      // 一帧中途断开时 tcpBuf 会残留半帧，重连后新帧拼在后面导致协议错位。
      tcpBuf = new Uint8Array(0);
      tcpHead = 0;
      frameChain = Promise.resolve();
      peers.clear();
      showOverlay();
      setStatus('连接断开，正在重连…', 'warn');
      renderAll();
      scheduleReconnect();
    };
    sock.onerror = () => { if (sock === ws) { try { sock.close(); } catch (e) {} } };
    sock.onmessage = onMessage;
  } catch (e) {
    // loadWsConfig / WebSocket 构造失败时兜底重连，避免 async 函数抛出未捕获的 rejection
    setStatus('连接异常，正在重连…', 'warn');
    scheduleReconnect();
  } finally {
    connecting = false;
  }
}

function setStatus(text, cls) {
  const el = document.getElementById('status');
  el.textContent = text;
  el.className = 'status ' + (cls || '');
}

// ---------- 接收并解析二进制流 ----------
let tcpBuf = new Uint8Array(0);
let tcpHead = 0;  // 已消费偏移：避免每条 WS 消息都把未消费部分整体 slice 复制
function onMessage(e) {
  const incoming = new Uint8Array(e.data);
  const unused = tcpBuf.length - tcpHead;
  // 拼出"未消费部分 + 新数据"：把残留挪到新缓冲开头，之后本轮内用 head 推进，不再反复 slice
  const next = new Uint8Array(unused + incoming.length);
  if (unused > 0) next.set(tcpBuf.subarray(tcpHead), 0);
  next.set(incoming, unused);
  tcpBuf = next;
  tcpHead = 0;

  while (tcpBuf.length - tcpHead >= 4) {
    const dv = new DataView(tcpBuf.buffer, tcpBuf.byteOffset + tcpHead, tcpBuf.length - tcpHead);
    const bodyLen = dv.getUint32(0, true);
    if (bodyLen < 1 || bodyLen > 64 * 1024 * 1024) { tcpBuf = new Uint8Array(0); tcpHead = 0; break; }
    const total = 4 + bodyLen;
    if (tcpBuf.length - tcpHead < total) break;
    const type = tcpBuf[tcpHead + 4];
    // payload 会交给异步解码器持有，必须独立拷贝；其余解析不再复制
    const payload = tcpBuf.slice(tcpHead + 5, tcpHead + total);
    tcpHead += total;
    try { dispatch(type, payload); } catch (e) { /* 单帧解析失败不应中断后续帧 */ }
  }
  if (tcpHead === tcpBuf.length) { tcpBuf = new Uint8Array(0); tcpHead = 0; }
  else if (tcpHead > 0 && tcpHead > tcpBuf.length / 2) {
    // 残留已过半则压缩一次，防止 head 长期推进导致缓冲无限增长
    const remain = tcpBuf.length - tcpHead;
    const compacted = new Uint8Array(remain);
    compacted.set(tcpBuf.subarray(tcpHead), 0);
    tcpBuf = compacted; tcpHead = 0;
  }
}

// 区域差分帧只含相对上一帧的变化块，必须全部按序解码：丢弃中间任何一帧都会留下
// 永久残影，直到下一次整帧重同步（默认 4 秒）。所以这里严格串行、一帧不丢。
let frameChain = Promise.resolve();
function dispatch(type, payload) {
  switch (type) {
    case 3:
      frameChain = frameChain.then(() => decodeFrame(payload)).catch(() => {});
      break;                                             // VideoFrame
    case 16: updateCursorData(payload); break;           // CursorData
    case 17: updateCursorPosition(payload); break;       // CursorPosition
    case 50: handlePeerCursor(payload); break;           // 某浏览器虚拟光标位置
    case 51: handlePeerList(payload); break;             // 欢迎包（含自身 id 与同伴）
    case 52: handlePeerLeave(payload); break;            // 某浏览器离开
    case 21: handleAuthResult(payload); break;          // 鉴权结果
    case 7: handleRemoteClipboard(payload); break;      // 剪贴板文本（远端 -> 浏览器）
    default: break;
  }
}

// 远端剪贴板文本：payload = [flag=1][int32 LE UTF-8 长度][UTF-8 文本]
function handleRemoteClipboard(payload) {
  if (!payload || payload.length < 5 || payload[0] !== 1) return;
  const len = (payload[1] | payload[2] << 8 | payload[3] << 16 | payload[4] << 24) >>> 0;
  if (len > 16 * 1024 * 1024 || 5 + len > payload.length) return;
  let text = '';
  try { text = new TextDecoder('utf-8').decode(payload.subarray(5, 5 + len)); } catch (_) { return; }
  const box = document.getElementById('clipText');
  if (box) box.value = text;
  // 尽量写入浏览器剪贴板（需 HTTPS/localhost 或用户手势上下文，失败不报错）
  try { if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).catch(() => {}); } catch (_) {}
  setStatus('已取回远端剪贴板文本', 'ok');
}

// ---------- 视频帧解码 ----------
async function decodeFrame(payload) {
  if (payload.length < 11) return;
  const flags = payload[0];
  const codec = payload[1];
  const w = (payload[2] | payload[3] << 8 | payload[4] << 16 | payload[5] << 24) >>> 0;
  const h = (payload[6] | payload[7] << 8 | payload[8] << 16 | payload[9] << 24) >>> 0;
  imgW = w; imgH = h;

  try {
    if (codec === 0 && (flags & 1)) {
      await drawRegions(payload, w, h);
    } else if (codec === 2) {
      const inf = await inflateRaw(payload.subarray(14));
      if (!inf) return;
      if (!(w > 0 && h > 0 && w <= 8192 && h <= 8192)) return;
      if (inf.length !== w * h * 4) return;               // 畸形帧尺寸与像素数不符，丢弃
      for (let i = 0; i + 2 < inf.length; i += 4) {       // 被控端原始整帧为 BGRA -> RGBA
        const b = inf[i]; inf[i] = inf[i + 2]; inf[i + 2] = b;
      }
      ensureVideoSize(w, h);
      vctx.putImageData(new ImageData(new Uint8ClampedArray(inf), w, h), 0, 0);
      present(w, h);
    } else {
      // 直接把 JPEG 字节包成 Blob：省掉 Base64 编码 + data URL 解析（每帧约 1.33 倍字符串与一次 fetch）
      const bm = await createImageBitmap(new Blob([payload.subarray(14)], { type: 'image/jpeg' }));
      ensureVideoSize(w, h);
      vctx.drawImage(bm, 0, 0, w, h);
      bm.close && bm.close();   // 整帧 60fps 下不释放会持续堆积大位图
      present(w, h);
    }
  } catch (err) { }
}

async function drawRegions(payload, w, h) {
  ensureVideoSize(w, h);
  const jpegBlocks = (payload[0] & 2) !== 0;
  // 帧头: flags(1)+codec(1)+w(4)+h(4)+blockCount(4) = 14 字节；每块头 X(4)Y(4)W(4)H(4)len(4) = 20 字节（小端，与 Protocol.ReadInt32 对齐）
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  let off = 14;
  const blocks = [];
  while (off + 20 <= payload.length) {
    const bx = dv.getUint32(off, true); off += 4;
    const by = dv.getUint32(off, true); off += 4;
    const bw = dv.getUint32(off, true); off += 4;
    const bh = dv.getUint32(off, true); off += 4;
    const len = dv.getUint32(off, true); off += 4;
    if (off + len > payload.length) break;
    blocks.push({ x: bx, y: by, w: bw, h: bh, data: payload.subarray(off, off + len), jpeg: jpegBlocks });
    off += len;
  }
  for (const b of blocks) {
    try {
      if (b.jpeg) {
        // 区域块通常一帧几十个：逐块走 Base64+fetch 会在主线程制造大量字符串与 Promise
        const bm = await createImageBitmap(new Blob([b.data], { type: 'image/jpeg' }));
        vctx.drawImage(bm, b.x, b.y, b.w, b.h);
        bm.close && bm.close();
      } else {
        // 降级块是未压缩的 BGRA 像素（服务端直接 BlockCopy，不压缩）。
        // 旧代码误当作 deflate 解压，必然失败并丢块；这里直接按原始像素处理并交换 B/R。
        let inf = b.data;
        if (inf.length !== b.w * b.h * 4) {
          // 兼容旧服务端/其它实现可能发来的 deflate-raw 块
          inf = await inflateRaw(b.data);
          if (!inf) continue;
        }
        if (!(b.w > 0 && b.h > 0 && b.w <= 8192 && b.h <= 8192) || inf.length !== b.w * b.h * 4) continue;
        const rgba = new Uint8ClampedArray(inf.length);
        rgba.set(inf);
        for (let i = 0; i + 2 < rgba.length; i += 4) { const t = rgba[i]; rgba[i] = rgba[i + 2]; rgba[i + 2] = t; }
        const imgData = new ImageData(rgba, b.w, b.h);
        const c2 = document.createElement('canvas');
        c2.width = b.w; c2.height = b.h;
        c2.getContext('2d').putImageData(imgData, 0, 0);
        vctx.drawImage(c2, b.x, b.y, b.w, b.h);
      }
    } catch (e) {}
  }
  present(w, h);
}

function ensureVideoSize(w, h) {
  if (videoCanvas.width !== w || videoCanvas.height !== h) {
    videoCanvas.width = w; videoCanvas.height = h;
  }
}

function present(w, h) {
  if (!(w > 0 && h > 0)) return;
  const cw = canvas.width, ch = canvas.height;
  const scale = Math.min(cw / w, ch / h);
  vdw = w * scale; vdh = h * scale;
  vdx = (cw - vdw) / 2; vdy = (ch - vdh) / 2;
  screenReady = true;
  renderAll();
}

function renderAll() {
  const cw = canvas.width, ch = canvas.height;
  ctx2d.fillStyle = '#000';
  ctx2d.fillRect(0, 0, cw, ch);
  if (screenReady && videoCanvas.width > 0 && videoCanvas.height > 0) {
    ctx2d.imageSmoothingEnabled = false;
    ctx2d.drawImage(videoCanvas, vdx, vdy, vdw, vdh);
  }
  drawServerCursor();
  drawPeers();
}

function requestRender() {
  if (renderPending) return;
  renderPending = true;
  requestAnimationFrame(() => { renderPending = false; renderAll(); });
}

// ---------- 远端真实光标（S端） ----------
function updateCursorData(payload) {
  if (payload.length < 14) return;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.length);
  const id = dv.getUint32(0, true);
  const hotX = payload[4], hotY = payload[5];
  const w = dv.getUint32(6, true), h = dv.getUint32(10, true);
  // 光标位图上限 256x256 且像素数必须与数据长度匹配，畸形帧直接丢弃，
  // 否则 new ImageData 会抛 RangeError 冒泡到 onMessage 中断收包循环。
  if (!(w > 0 && h > 0 && w <= 256 && h <= 256)) return;
  if (payload.length - 14 !== w * h * 4) return;
  const rgba = payload.subarray(14);
  const imgData = new ImageData(new Uint8ClampedArray(rgba), w, h);
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  c.getContext('2d').putImageData(imgData, 0, 0);
  cursor.bmp = c; cursor.hotX = hotX; cursor.hotY = hotY; cursor.w = w; cursor.h = h;
  cursor.lastId = id;
  if (screenReady) requestRender();
}

function updateCursorPosition(payload) {
  if (payload.length < 4) return;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.length);
  cursor.x = dv.getUint16(0, true);
  cursor.y = dv.getUint16(2, true);
  cursor.visible = true;
  if (screenReady) requestRender();
}

function drawServerCursor() {
  if (!cursor.visible || !cursor.bmp || !screenReady) return;
  if (!(imgW > 0 && vdw > 0 && vdh > 0)) return;
  const sc = vdw / imgW;
  const px = vdx + cursor.x / 65535 * vdw;
  const py = vdy + cursor.y / 65535 * vdh;
  ctx2d.drawImage(cursor.bmp, px - cursor.hotX * sc, py - cursor.hotY * sc, cursor.w * sc, cursor.h * sc);
}

// ---------- 多鼠标（同伴光标） ----------
function handlePeerCursor(payload) {
  if (payload.length < 9) return;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.length);
  const id = dv.getUint32(0, true);
  const x = dv.getUint16(4, true);
  const y = dv.getUint16(6, true);
  const ipLen = payload[8];
  const ip = new TextDecoder().decode(payload.subarray(9, 9 + ipLen));
  peers.set(id, { ip, x, y });
  requestRender();
}

function handlePeerList(payload) {
  if (payload.length < 6) return;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.length);
  myId = dv.getUint32(0, true);
  const count = dv.getUint16(4, true);
  let off = 6;
  peers.clear();
  for (let i = 0; i < count; i++) {
    if (off + 9 > payload.length) break;
    const id = dv.getUint32(off, true); off += 4;
    const x = dv.getUint16(off, true); off += 2;
    const y = dv.getUint16(off, true); off += 2;
    const ipLen = payload[off]; off += 1;
    const ip = new TextDecoder().decode(payload.subarray(off, off + ipLen)); off += ipLen;
    peers.set(id, { ip, x, y });
  }
  requestRender();
}

function handlePeerLeave(payload) {
  if (payload.length < 4) return;
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.length);
  peers.delete(dv.getUint32(0, true));
  requestRender();
}

function peerColor(id) {
  return 'hsl(' + ((id * 137.508) % 360).toFixed(0) + ',75%,55%)';
}

function drawPeers() {
  for (const [id, p] of peers) {
    if (!p || p.x == null) continue;
    const px = vdx + (p.x / 65535) * vdw;
    const py = vdy + (p.y / 65535) * vdh;
    const col = peerColor(id);
    ctx2d.beginPath();
    ctx2d.arc(px, py, 7, 0, Math.PI * 2);
    ctx2d.fillStyle = col; ctx2d.fill();
    ctx2d.lineWidth = 2; ctx2d.strokeStyle = '#fff'; ctx2d.stroke();
    const label = p.ip + (id === myId ? ' (你)' : '');
    ctx2d.font = '13px sans-serif';
    const tw = ctx2d.measureText(label).width;
    ctx2d.fillStyle = 'rgba(0,0,0,0.55)';
    ctx2d.fillRect(px + 10, py - 20, tw + 8, 18);
    ctx2d.fillStyle = col;
    ctx2d.fillText(label, px + 14, py - 6);
  }
}

// ---------- 发送输入 ----------
// 鼠标消息：MouseEvent(4) 的 payload = [sub(1)][xNorm(2)][yNorm(2)][btn(1)][wheel(2)]
function sendMouse(subtype, btn, x, y, wheel) {
  if (!connected || !ws || ws.readyState !== 1) return;
  const pkt = new ArrayBuffer(13);   // 4(len) + 1(type) + 8(payload)
  const dv = new DataView(pkt);
  dv.setUint32(0, 9, true);         // bodyLen = 1(type) + 8(payload)
  dv.setUint8(4, 4);                // MouseEvent
  dv.setUint8(5, subtype);
  dv.setUint16(6, x & 0xffff, true);
  dv.setUint16(8, y & 0xffff, true);
  dv.setUint8(10, btn);
  dv.setInt16(11, wheel | 0, true);
  ws.send(pkt);
}

// 虚拟光标（仅悬停）：type=60，payload=[xNorm(2)][yNorm(2)]
function sendVirtual(x, y) {
  if (!connected || !ws || ws.readyState !== 1) return;
  const pkt = new ArrayBuffer(9);    // 4(len) + 1(type=60) + 4(payload)
  const dv = new DataView(pkt);
  dv.setUint32(0, 5, true);
  dv.setUint8(4, 60);
  dv.setUint16(5, x & 0xffff, true);
  dv.setUint16(7, y & 0xffff, true);
  ws.send(pkt);
}

function sendControl(type, payload) {
  if (!connected || !ws || ws.readyState !== 1) return;
  const buf = new ArrayBuffer(4 + 1 + payload.length);
  const dv = new DataView(buf);
  dv.setUint32(0, 1 + payload.length, true);
  dv.setUint8(4, type);
  new Uint8Array(buf, 5).set(payload);
  ws.send(buf);
}

// ---------- 画质档位 / 控制动作 ----------
// 布局与 QualityConfig.Encode 一致：[mode=0(JpegRegions), q, width(int32 LE 4字节), fps, resyncSec, blockSize]
// 服务端 ClampQualityForOs 会在 Win7 上自动封顶（≤1280/12fps/q60），所以这里放心发高档。
const QUALITY_PRESETS = {
  smooth: [0, 40, 192, 3, 0, 0, 24, 4, 32],   // 960px  / q40 / 24fps —— 弱网、Win7 友好
  normal: [0, 65, 0, 5, 0, 0, 30, 4, 32],     // 1280px / q65 / 30fps —— 默认
  hd:     [0, 80, 64, 6, 0, 0, 60, 4, 32]     // 1600px / q80 / 60fps —— 高清
  // width 是 int32 小端：192+3*256=960, 0+5*256=1280, 64+6*256=1600
};

function currentQuality() {
  const q = document.getElementById('quality');
  return (q && QUALITY_PRESETS[q.value]) ? q.value : 'normal';
}

function applyQuality() {
  if (!authed) return;
  const preset = QUALITY_PRESETS[currentQuality()];
  if (!preset) return;
  sendControl(10, new Uint8Array(preset));
  try { localStorage.setItem('painterQuality', currentQuality()); } catch (_) {}
}

function sendControlAction(id) {
  if (!authed) return;
  sendControl(19, new Uint8Array([id]));  // ControlAction: 1=CtrlAltDel, 2=LockScreen, 6=ReadClipboard
}

// 把文本框内容写入被控端剪贴板：type=7 [flag=1][int32 LE UTF-8 长度][UTF-8 字节]
function sendClipboardText(text) {
  if (!authed || typeof text !== 'string' || !text.length) return;
  const bytes = new TextEncoder().encode(text);
  if (bytes.length > 16 * 1024 * 1024) return;
  const body = new Uint8Array(1 + 4 + bytes.length);
  body[0] = 1;
  const dv = new DataView(body.buffer);
  dv.setUint32(1, bytes.length, true);
  body.set(bytes, 5);
  sendControl(7, body);
  setStatus('已发送文本到远端剪贴板', 'ok');
}

// ---------- 访问密码鉴权 ----------
function sendAuth() {
  if (!connected || !ws || ws.readyState !== 1) return;
  const bytes = new TextEncoder().encode(document.getElementById('pwd').value || '');
  const buf = new ArrayBuffer(4 + 1 + bytes.length);
  const dv = new DataView(buf);
  dv.setUint32(0, 1 + bytes.length, true);
  dv.setUint8(4, 20);                 // CtrlAuth
  new Uint8Array(buf, 5).set(bytes);
  ws.send(buf);
}

function handleAuthResult(payload) {
  if (payload.length < 1) return;
  if (payload[0] === 1) {
    authed = true;
    hideOverlay();
    setStatus('已连接', 'ok');
    // 按用户选择的画质档位通知被控端（Win7 上服务端会自动封顶到安全参数）
    applyQuality();
  } else if (payload[0] === 0) {
    authed = false;
    showOverlay();
    setAuthMsg('密码错误，请重试');
  }
  // payload[0]===2：服务器提示"需要密码"，登录框本就可见，无需处理
}

function showOverlay() { document.getElementById('overlay').classList.remove('hidden'); }
function hideOverlay() { document.getElementById('overlay').classList.add('hidden'); }
function setAuthMsg(s) { const el = document.getElementById('authmsg'); if (el) el.textContent = s || ''; }

// 画布 CSS 坐标 -> 被控端归一化坐标(0..65535)
//
// 画面不是铺满画布的：present() 按 contain 方式等比缩放并居中，两侧留黑边，
// 实际绘制区域是 (vdx, vdy, vdw, vdh)。v* 是画布「背板像素」（已乘 dpr），
// 而 pointer 事件给出的是「CSS 像素」，两者差一个比例。
// 因此正确换算是：CSS px -> 背板 px -> 减去黑边偏移 -> 除以画面实际尺寸。
// 早前这里直接用 cx/rect.width 归一化整个画布，等于假设画面铺满，
// 于是屏幕比例非 16:9（出现黑边）时，点击位置会整体偏移，
// 必须点到画面外的黑边上才能碰到屏幕边缘。
function norm(cx, cy) {
  if (!screenReady || vdw <= 0 || vdh <= 0) return [0, 0];
  const rect = canvas.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) return [0, 0];
  // 用实测比例而非直接用 dpr：canvas.width 有 Math.round，与 rect.width 不一定严格成比例
  const bx = cx * (canvas.width / rect.width);
  const by = cy * (canvas.height / rect.height);
  const nx = (bx - vdx) / vdw;
  const ny = (by - vdy) / vdh;
  // 落在黑边上时钳到最近边缘（保持既有行为：黑边点击不失效）
  return [Math.round(Math.max(0, Math.min(1, nx)) * 65535),
          Math.round(Math.max(0, Math.min(1, ny)) * 65535)];
}

function avgY() {
  let s = 0;
  for (const p of pointers.values()) s += p.y;
  return s / Math.max(1, pointers.size);
}

// ---------- Pointer 事件（鼠标 + 触摸统一） ----------
function onPointerDown(e) {
  if (!connected) return;
  if (!authed) return;
  e.preventDefault();
  try { canvas.setPointerCapture(e.pointerId); } catch (_) {}
  // 点击画面后让隐藏输入框保持焦点，PC 端键盘与移动端软键盘/IME 才能输入到被控端。
  if (kbd && document.activeElement !== kbd) { try { kbd.focus({ preventScroll: true }); } catch (_) { try { kbd.focus(); } catch (_) {} } }
  const rect = canvas.getBoundingClientRect();
  const x = e.clientX - rect.left, y = e.clientY - rect.top;
  const p = { x, y, sx: x, sy: y, moved: false, longFired: false, inWheel: false };
  pointers.set(e.pointerId, p);

  if (e.button === 2) {           // 鼠标右键
    const [nx, ny] = norm(x, y);
    sendMouse(0, 0, nx, ny, 0);
    sendMouse(1, BTN_RIGHT, nx, ny, 0);
    p.isRight = true;
    return;
  }
  if (e.button === 1) {           // 鼠标中键
    const [nx, ny] = norm(x, y);
    sendMouse(0, 0, nx, ny, 0);
    sendMouse(1, BTN_MIDDLE, nx, ny, 0);
    p.isMiddle = true;
    return;
  }

  if (pointers.size === 1) {
    clearTimeout(longPressTimer);
    // 立即把光标瞬移到按下点并保持「按下即落笔」：以前等拖动超过 12px 才补发
    // 按下事件，导致每笔起点丢失、画的内容不准，且快速短划会整个丢掉。
    // 现在按下点先移动再按下，随后每次 move 都是跟手的绝对移动。
    const [dx, dy] = norm(x, y);
    sendMouse(0, 0, dx, dy, 0);
    sendMouse(1, BTN_LEFT, dx, dy, 0);
    leftDown = true;
    p.moved = true;
    longPressTimer = setTimeout(() => {
      const cur = pointers.get(e.pointerId);
      if (cur && pointers.size === 1 && !cur.dragMoved) {
        const [nx, ny] = norm(cur.x, cur.y);
        if (leftDown) { sendMouse(2, BTN_LEFT, nx, ny, 0); leftDown = false; }
        sendMouse(0, 0, nx, ny, 0);
        sendMouse(1, BTN_RIGHT, nx, ny, 0);   // 长按 = 右键
        sendMouse(2, BTN_RIGHT, nx, ny, 0);
        cur.longFired = true;
      }
    }, 550);
  } else if (pointers.size === 2) {
    clearTimeout(longPressTimer);
    if (leftDown) { const [nx, ny] = norm(x, y); sendMouse(2, BTN_LEFT, nx, ny, 0); leftDown = false; }
    wheelActive = true;
    wheelRefY = avgY();
  }
}

function onPointerMove(e) {
  if (!connected) return;
  if (!authed) return;
  e.preventDefault();
  const p = pointers.get(e.pointerId);
  if (!p) return;
  const rect = canvas.getBoundingClientRect();
  p.x = e.clientX - rect.left;
  p.y = e.clientY - rect.top;

  if (pointers.size >= 2) {        // 双指滚轮
    const ay = avgY();
    const dy = ay - wheelRefY;
    wheelRefY = ay;
    if (Math.abs(dy) >= 4) {
      const steps = Math.max(1, Math.round(Math.abs(dy) / 8));
      const dir = dy > 0 ? -1 : 1; // 上滑=上滚(正)，下滑=下滚(负)
      const [nx, ny] = norm(p.x, p.y);
      sendMouse(3, 0, nx, ny, dir * steps);
    }
    for (const q of pointers.values()) q.inWheel = true;
    return;
  }

  if (pointers.size === 1) {
    const [nx, ny] = norm(p.x, p.y);
    if (!p.longFired && (e.buttons & 1)) {
      // 每个 pointermove 立即跟手发送绝对移动，没有任何位移阈值——
      // 之前 >12px 才发一次，慢速小幅移动会整段丢失、笔画起点偏移。
      sendMouse(0, 0, nx, ny, 0);
      // 真正确认发生拖动后取消长按（避免画着画着冒出右键菜单）。
      if (!p.dragMoved && Math.hypot(p.x - p.sx, p.y - p.sy) > 4) {
        p.dragMoved = true;
        clearTimeout(longPressTimer);
      }
    } else {
      sendVirtual(nx, ny);                              // 悬停：仅更新虚拟光标
    }
  }
}

function onPointerUp(e) {
  if (!connected) return;
  if (!authed) return;
  const p = pointers.get(e.pointerId);
  if (!p) return;
  pointers.delete(e.pointerId);
  clearTimeout(longPressTimer);
  const rect = canvas.getBoundingClientRect();
  const x = e.clientX - rect.left, y = e.clientY - rect.top;

  if (p.isRight) { const [nx, ny] = norm(x, y); sendMouse(2, BTN_RIGHT, nx, ny, 0); return; }
  if (p.isMiddle) { const [nx, ny] = norm(x, y); sendMouse(2, BTN_MIDDLE, nx, ny, 0); return; }

  if (wheelActive || p.inWheel) {
    wheelActive = false;
    for (const q of pointers.values()) q.inWheel = true;
    if (leftDown) { const [nx, ny] = norm(x, y); sendMouse(2, BTN_LEFT, nx, ny, 0); leftDown = false; }
    return;
  }

  if (pointers.size >= 1) return;

  if (p.longFired) {
    // 右键已发
  } else if (p.moved) {
    if (leftDown) { const [nx, ny] = norm(x, y); sendMouse(2, BTN_LEFT, nx, ny, 0); leftDown = false; }  // 拖动结束
  } else {
    const [nx, ny] = norm(x, y);   // 轻触 = 左键单击一次
    sendMouse(1, BTN_LEFT, nx, ny, 0);
    sendMouse(2, BTN_LEFT, nx, ny, 0);
  }
}

// 滚轮（桌面鼠标）
function onWheel(e) {
  if (!connected) return;
  if (!authed) return;
  e.preventDefault();
  const rect = canvas.getBoundingClientRect();
  const [nx, ny] = norm(e.clientX - rect.left, e.clientY - rect.top);
  // Windows 按 WHEEL_DELTA=120 整数分格处理：浏览器 deltaY 一格约 100~120，
  // 旧代码只发 ±2~3，远端当成 0 格忽略。统一换算到 120 的整数倍。
  const normalized = e.deltaMode === 1 ? e.deltaY * 16 : (e.deltaMode === 2 ? e.deltaY * 400 : e.deltaY);
  const dir = normalized > 0 ? -1 : 1;  // 下滚=负(向下)，上滚=正(向上)
  const notches = Math.max(1, Math.min(30, Math.round(Math.abs(normalized) / 120)));
  sendMouse(3, 0, nx, ny, dir * notches * 120);
}

function releaseAll() {
  if (leftDown) { sendMouse(2, BTN_LEFT, 0, 0, 0); leftDown = false; }
  // 释放所有组合字符键，避免失焦后远端按键卡住
  modifierCharsDown.forEach(function (v) { sendKey(v, false); });
  modifierCharsDown.clear();
  pointers.clear();
  clearTimeout(longPressTimer);
  wheelActive = false;
}

// ---------- 键盘 ----------
const vkMap = {
  'Backspace': 0x08, 'Tab': 0x09, 'Enter': 0x0D, 'Shift': 0x10, 'Control': 0x11,
  'Alt': 0x12, 'Pause': 0x13, 'CapsLock': 0x14, 'Escape': 0x1B, 'Space': 0x20,
  'PageUp': 0x21, 'PageDown': 0x22, 'End': 0x23, 'Home': 0x24, 'Left': 0x25,
  'Up': 0x26, 'Right': 0x27, 'Down': 0x28, 'Insert': 0x2D, 'Delete': 0x2E,
  'Win': 0x5B, 'Meta': 0x5B, 'OS': 0x5B, 'ContextMenu': 0x5D,
  'F1': 0x70, 'F2': 0x71, 'F3': 0x72, 'F4': 0x73,
  'F5': 0x74, 'F6': 0x75, 'F7': 0x76, 'F8': 0x77, 'F9': 0x78, 'F10': 0x79,
  'F11': 0x7A, 'F12': 0x7B, 'NumLock': 0x90, 'ScrollLock': 0x91
};

function hasModifier(e) { return e.ctrlKey || e.altKey || e.metaKey; }
// 记录"带修饰键时以虚拟键方式发出的字符"，否则先松修饰键再松字符键时
// onKeyUp 的 hasModifier 为 false，字符键的 keyup 永远不发，远端按键卡住。
// 用物理键码 e.code 作键：切换输入法/键盘布局后 e.key 会变，按 e.key 匹配会失配卡键。
const modifierCharsDown = new Map();  // e.code -> 发送的虚拟键

function onKeyDown(e) {
  if (!connected || !authed) return;
  // IME 组字中（中文/日文等）：交给隐藏输入框，composition 结束后由 input 事件发送完整文本
  if (e.isComposing || e.key === 'Process') return;
  // 控制键 / 功能键 / 带 Ctrl+Alt+Win 的组合键：走虚拟键通道（保留组合键语义）
  const vk = vkOf(e);
  if (vk !== null) { sendKey(vk, true); e.preventDefault(); return; }
  if (hasModifier(e) && e.key && e.key.length === 1) {
    const v = e.key.toUpperCase().charCodeAt(0);
    if (v <= 0x7F) { sendKey(v, true); modifierCharsDown.set(e.code || ('char' + v), v); e.preventDefault(); }
  }
  // 普通可打印字符不在这里拦截，让它进入隐藏输入框，由 onKbdInput 走 Unicode 通道，
  // 这样中文、表情、移动端软键盘与粘贴文本都能正确注入。
}
function onKeyUp(e) {
  if (!connected || !authed) return;
  const vk = vkOf(e);
  if (vk !== null) { sendKey(vk, false); e.preventDefault(); return; }
  // 优先按"按下时记录的虚拟键"释放：先松修饰键、或布局变化导致 e.key 不同，都能正确抬键
  const downVk = modifierCharsDown.get(e.code || '');
  if (downVk !== undefined) {
    modifierCharsDown.delete(e.code || '');
    sendKey(downVk, false); e.preventDefault(); return;
  }
  if (hasModifier(e) && e.key && e.key.length === 1) {
    const v = e.key.toUpperCase().charCodeAt(0);
    if (v <= 0x7F) { sendKey(v, false); e.preventDefault(); }
  }
}
function vkOf(e) {
  return (e.key && vkMap[e.key] !== undefined) ? vkMap[e.key] : null;
}

// 隐藏输入框：捕获 IME 结果 / 软键盘 / 粘贴，按 UTF-16 码元发 UnicodeText(type=50)。
function onKbdInput() {
  if (!kbd) return;
  const text = kbd.value;
  kbd.value = '';
  if (text && connected && authed) sendUnicodeText(text);
}

function sendUnicodeText(text) {
  if (!ws || ws.readyState !== 1) return;
  // payload = UTF-16LE 码元序列；服务端用 Encoding.Unicode.GetString 还原。
  const bytes = new Uint8Array(text.length * 2);
  for (let i = 0; i < text.length; i++) {
    bytes[i * 2] = text.charCodeAt(i) & 0xff;
    bytes[i * 2 + 1] = (text.charCodeAt(i) >> 8) & 0xff;
  }
  const buf = new ArrayBuffer(4 + 1 + bytes.length);
  const dv = new DataView(buf);
  dv.setUint32(0, 1 + bytes.length, true);
  dv.setUint8(4, 50);                // UnicodeText
  new Uint8Array(buf, 5).set(bytes);
  ws.send(buf);
}

function sendKey(vk, down) {
  const buf = new ArrayBuffer(4 + 1 + 5);
  const dv = new DataView(buf);
  dv.setUint32(0, 1 + 5, true);
  dv.setUint8(4, 5);                // KeyEvent
  dv.setUint32(5, vk, true);
  dv.setUint8(9, down ? 1 : 0);
  if (ws && ws.readyState === 1) ws.send(buf);
}

// ---------- 工具 ----------
function bytesToBase64(bytes) {
  let bin = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk)
    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  return btoa(bin);
}

// 原始 RGBA 帧(被控端 JPEG 不可用时才会下发)用 DEFLATE 压缩，这里解压。
async function inflateRaw(input) {
  if (typeof DecompressionStream === 'undefined') return null;
  try {
    const ds = new DecompressionStream('deflate-raw');
    const stream = new Blob([input]).stream().pipeThrough(ds);
    const buf = await new Response(stream).arrayBuffer();
    return new Uint8Array(buf);
  } catch (e) { return null; }
}

window.addEventListener('DOMContentLoaded', init);
