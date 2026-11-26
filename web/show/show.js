// 图片展示 · 手机遥控端逻辑
(function () {
  "use strict";

  var code = new URLSearchParams(location.search).get("code") || "";
  var ws = null;
  var reconnectTimer = null;
  var state = { open: false, count: 0, index: 0, zoom: 1, offX: 0, offY: 0, screenW: 1920, screenH: 1080 };
  // 手机本地保留已上传图片副本，便于在本机预览 / 缩略图点选（与电脑大屏显示的是同一批）
  var localImages = [];

  var $ = function (id) { return document.getElementById(id); };
  var statusEl = $("status");
  var stage = $("stage");
  var curImg = $("cur");
  var emptyEl = $("empty");
  var idxEl = $("idx");
  var totalEl = $("total");
  var thumbsEl = $("thumbs");

  function setStatus(text, cls) {
    statusEl.textContent = text;
    statusEl.className = "status" + (cls ? " " + cls : "");
  }

  // ---------- WebSocket：状态同步 + 指令下发 ----------
  // Windows 7 的 HTTP.sys 不支持 WebSocket，服务端会另开一条 TCP 兼容通道；端口来自 /wsconfig
  var wsCfg = null;
  // 每次连接/重连都重新取 /wsconfig：Win7 兼容通道可能稍后才启动成功，
  // 缓存一次性 Promise 会让 wsPort=0 的结果永久卡死。
  function loadWsConfig() {
    return fetch("/wsconfig", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .catch(function () { return null; });
  }

  function wsHost() {
    var h = location.hostname;
    if (h.indexOf(":") >= 0 && h.charAt(0) !== "[") h = "[" + h + "]";
    var p = (wsCfg && wsCfg.wsPort) ? wsCfg.wsPort : location.port;
    return p ? h + ":" + p : h;
  }

  function connect() {
    loadWsConfig().then(function (cfg) {
      if (!cfg) { scheduleReconnect(); return; }
      wsCfg = cfg;
      if (cfg.needCompat && !cfg.compat && cfg.error) {
        setStatus("兼容通道未就绪：" + cfg.error, "err");
      }
      var proto = location.protocol === "https:" ? "wss" : "ws";
      var url = proto + "://" + wsHost() + "/show/ws" + (code ? "?code=" + encodeURIComponent(code) : "");
      try { ws = new WebSocket(url); } catch (e) { scheduleReconnect(); return; }

      ws.onopen = function () { setStatus("已连接", "ok"); };
      ws.onclose = function () { setStatus("连接断开，重连中…", "err"); scheduleReconnect(); };
      ws.onerror = function () { setStatus("连接异常", "err"); };
      ws.onmessage = function (ev) {
        try {
          var msg = JSON.parse(ev.data);
          if (msg && msg.type === "state") {
            state = msg;
            render();
          }
        } catch (e) {}
      };
    });
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(function () { reconnectTimer = null; connect(); }, 2000);
  }

  function send(obj) {
    if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
  }

  // ---------- 渲染 ----------
  function render() {
    totalEl.textContent = state.count;
    idxEl.textContent = state.count > 0 ? (state.index + 1) : 0;
    var img = (state.index >= 0 && state.index < localImages.length) ? localImages[state.index] : null;
    if (img) {
      // src 未变化时不重设，否则每次状态更新都会重新解码图片造成闪烁/卡顿
      if (curImg.getAttribute("src") !== img.url) curImg.src = img.url;
      curImg.style.display = "block";
      emptyEl.style.display = "none";
    } else {
      curImg.style.display = "none";
      emptyEl.style.display = "block";
      emptyEl.textContent = state.open ? "电脑大屏暂无此图（请用缩略图选择）" : "大屏预览已关闭";
    }
    setStatus(state.open ? ("已连接 · 共 " + state.count + " 张") : "已连接 · 大屏预览关闭", "ok");
    if (toggleScreenEl) {
      toggleScreenEl.textContent = state.open ? "■ 关闭大屏显示" : "▶ 打开大屏显示";
      toggleScreenEl.classList.toggle("on", !!state.open);
    }
    renderThumbs();
  }

  var thumbsKey = "";
  function renderThumbs() {
    // 状态消息约 30Hz 到达，但缩略图列表只在图片增删时才需要重建；
    // 仅切换选中项时只改 class，避免每次销毁/重建所有 img 触发解码与布局抖动。
    var key = localImages.length + ":" + localImages.map(function (im) { return im.url; }).join("|");
    if (key !== thumbsKey) {
      thumbsKey = key;
      thumbsEl.innerHTML = "";
      localImages.forEach(function (im, i) {
        var el = document.createElement("img");
        el.src = im.url;
        el.onclick = function () { send({ action: "goto", index: i }); };
        thumbsEl.appendChild(el);
      });
    }
    var kids = thumbsEl.children;
    for (var i = 0; i < kids.length; i++) {
      var on = i === state.index;
      if (kids[i].classList.contains("active") !== on) kids[i].classList.toggle("active", on);
    }
  }

  // ---------- 传输队列：逐张上传 + 状态 + 删除（修复手机端“选了没反应”，并支持误传删除） ----------
  var queue = [];          // { id, file, name, url, size, status, abort, serverObj }
  var qid = 0;
  var uploading = false;
  var transferListEl = $("transferList");
  var transferCountEl = $("transferCount");
  var toggleScreenEl = $("toggleScreen");

  function fmtSize(n) {
    if (n >= 1048576) return (n / 1048576).toFixed(1) + " MB";
    if (n >= 1024) return (n / 1024).toFixed(0) + " KB";
    return n + " B";
  }

  function statusText(s) {
    return ({ queued: "排队中", uploading: "上传中…", done: "已完成", failed: "失败", canceled: "已取消" })[s] || s;
  }

  function isImageFile(f) {
    var t = (f.type || "").toLowerCase();
    if (t.indexOf("image/") === 0) return true;
    // 部分安卓文件选择器返回的 type 为空，用扩展名兜底，避免列表空白
    var n = (f.name || "").toLowerCase();
    return /\.(jpe?g|png|gif|bmp|webp|heic|heif|tiff?)$/.test(n);
  }

  function addToQueue(files) {
    for (var i = 0; i < files.length; i++) {
      var f = files[i];
      if (!isImageFile(f)) continue;     // 只接受图片（type 为空时用扩展名兜底）
      queue.push({ id: ++qid, file: f, name: f.name, url: URL.createObjectURL(f), size: f.size, status: "queued", abort: null, serverObj: null, _timedOut: false });
    }
    renderQueue();
    startUpload();
  }

  function nextQueued() {
    for (var i = 0; i < queue.length; i++) if (queue[i].status === "queued") return queue[i];
    return null;
  }

  function renderQueue() {
    if (!transferListEl) return;
    transferListEl.innerHTML = "";
    if (transferCountEl) transferCountEl.textContent = queue.length;
    queue.forEach(function (it) {
      var li = document.createElement("li");
      li.className = "t-item t-" + it.status;

      var thumb = document.createElement("img");
      thumb.className = "t-thumb";
      thumb.src = it.url;
      li.appendChild(thumb);

      var info = document.createElement("div");
      info.className = "t-info";
      var nm = document.createElement("div");
      nm.className = "t-name";
      nm.textContent = it.name;
      var meta = document.createElement("div");
      meta.className = "t-meta";
      meta.textContent = fmtSize(it.size) + " · " + statusText(it.status);
      info.appendChild(nm); info.appendChild(meta);
      li.appendChild(info);

      var del = document.createElement("button");
      del.className = "t-del";
      del.textContent = "移除";
      del.title = "删除";
      del.onclick = (function (item) { return function () { removeItem(item); }; })(it);
      li.appendChild(del);

      transferListEl.appendChild(li);
    });
  }

  function removeItem(it) {
    var i = queue.indexOf(it);
    if (i < 0) return;
    // 已传到大屏的：按当前顺序索引从服务器一并删除（手机端 localImages 与服务器顺序锁步，删除同一位置即可重排）
    if (it.status === "done" && it.serverObj) {
      var li = localImages.indexOf(it.serverObj);
      if (li >= 0) { send({ action: "delete", index: li }); localImages.splice(li, 1); }
    }
    if (it.abort) { try { it.abort.abort(); } catch (e) {} }
    queue.splice(i, 1);
    renderQueue();
    render();
  }

  function startUpload() {
    if (uploading) return;
    uploading = true;
    processQueue();
  }

  function processQueue() {
    var it = nextQueued();
    if (!it) { uploading = false; setStatus(state.open ? ("已连接 · 共 " + state.count + " 张") : "已连接 · 大屏预览关闭", "ok"); return; }
    it.status = "uploading";
    renderQueue();
    uploadOne(it).then(function (j) {
      if (j && j.ok) {
        it.status = "done";
        var obj = { name: it.name, url: it.url };
        it.serverObj = obj;
        localImages.push(obj);
        setStatus("已上传 " + j.added + " 张，共 " + j.count + " 张", "ok");
      } else {
        it.status = "failed";
        setStatus("上传失败：" + ((j && j.error) || "未知错误"), "err");
      }
      renderQueue();
      processQueue();
    }).catch(function (e) {
      if (e && e.name === "AbortError") it.status = it._timedOut ? "failed" : "canceled";
      else { it.status = "failed"; setStatus("上传异常：" + (e && e.message || e), "err"); }
      renderQueue();
      processQueue();
    });
  }

  // 客户端等比缩放到 2000px 再上传（JPEG 0.85），避免手机原图过大导致安卓端上传失败/超时
  function uploadOne(it) {
    var ac = (typeof AbortController !== "undefined") ? new AbortController() : null;
    it.abort = ac;
    return new Promise(function (resolve, reject) {
      var img = new Image();
      img.onerror = function () { reject(new Error("图片解码失败")); };
      img.onload = function () {
        try {
          var max = 2000;
          var w = img.naturalWidth || 0, h = img.naturalHeight || 0;
          var scale = (w > 0 && h > 0) ? Math.min(1, max / Math.max(w, h)) : 1;
          var cw = Math.max(1, Math.round(w * scale)), ch = Math.max(1, Math.round(h * scale));
          var canvas = document.createElement("canvas");
          canvas.width = cw; canvas.height = ch;
          var c2 = canvas.getContext("2d");
          c2.drawImage(img, 0, 0, cw, ch);
          var dataURL = canvas.toDataURL("image/jpeg", 0.85);
          var base64 = String(dataURL).split(",")[1] || "";
          var to = setTimeout(function () { it._timedOut = true; if (ac) ac.abort(); }, 60000);
          fetch("/show/api/upload" + (code ? "?code=" + encodeURIComponent(code) : ""), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ images: [{ name: it.name, data: base64 }] }),
            signal: ac ? ac.signal : undefined
          }).then(function (resp) { clearTimeout(to); return resp.json(); })
            .then(resolve).catch(function (e) { clearTimeout(to); reject(e); });
        } catch (e) { reject(e); }
      };
      img.src = it.url;
    });
  }

  $("pick").addEventListener("click", function () { $("file").click(); });
  if (toggleScreenEl) toggleScreenEl.addEventListener("click", function () {
    send({ action: state.open ? "exit" : "open" });   // 与服务端 ShowController.Open/Close 对应
  });
  $("file").addEventListener("change", function (e) {
    if (e.target.files && e.target.files.length) addToQueue(e.target.files);
    e.target.value = "";   // 允许再次选择同一文件
  });

  // ---------- 控制按钮 ----------
  var acts = {
    prev: function () { send({ action: "prev" }); },
    next: function () { send({ action: "next" }); },
    zoomin: function () { send({ action: "zoom", factor: 1.2 }); },
    zoomout: function () { send({ action: "zoom", factor: 1 / 1.2 }); },
    reset: function () { send({ action: "reset" }); },
    exit: function () { send({ action: "exit" }); }
  };
  Array.prototype.forEach.call(document.querySelectorAll(".controls button"), function (b) {
    var a = b.getAttribute("data-act");
    if (acts[a]) b.addEventListener("click", acts[a]);
  });

  // ---------- 在画面上拖动 / 双指捏合 → 平移 / 缩放大屏视角 ----------
  var pointers = {};
  var lastDist = 0;

  stage.addEventListener("pointerdown", function (e) {
    pointers[e.pointerId] = { x: e.clientX, y: e.clientY };
    stage.setPointerCapture(e.pointerId);
  });
  stage.addEventListener("pointermove", function (e) {
    if (!pointers[e.pointerId]) return;
    var prev = pointers[e.pointerId];
    var ids = Object.keys(pointers);
    if (ids.length >= 2) {
      // 双指捏合缩放
      var p1 = pointers[ids[0]], p2 = pointers[ids[1]];
      var d = Math.hypot(p1.x - p2.x, p1.y - p2.y);
      if (lastDist > 0 && d > 0) {
        send({ action: "zoom", factor: d / lastDist });
      }
      lastDist = d;
    } else {
      // 单指拖动平移
      var dx = e.clientX - prev.x;
      var dy = e.clientY - prev.y;
      var rect = stage.getBoundingClientRect();
      var sx = (dx / Math.max(1, rect.width)) * state.screenW;
      var sy = (dy / Math.max(1, rect.height)) * state.screenH;
      send({ action: "pan", dx: Math.round(sx), dy: Math.round(sy) });
    }
    pointers[e.pointerId] = { x: e.clientX, y: e.clientY };
  });
  function endPointer(e) { delete pointers[e.pointerId]; lastDist = 0; }
  stage.addEventListener("pointerup", endPointer);
  stage.addEventListener("pointercancel", endPointer);
  stage.addEventListener("pointerleave", endPointer);

  // ---------- 启动 ----------
  render();
  connect();
})();
