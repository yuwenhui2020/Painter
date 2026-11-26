// /share —— 图片上传（内置白板 / PPT 放映叠加）+ 放映图片显隐控制
(function () {
  "use strict";

  var code = new URLSearchParams(location.search).get("code") || "";
  var qs = code ? "?code=" + encodeURIComponent(code) : "";
  var $ = function (id) { return document.getElementById(id); };

  var state = {
    open: false, count: 0, index: 0, angle: 0,
    pptMode: false, pptSlide: 0, pptItems: []
  };
  var localImages = [];      // 本机预览（与上传顺序一致）
  var queue = [];            // 传输队列
  var uploading = false;
  var ws = null, reconnectTimer = null, wsCfg = null;

  function setStatus(text, cls) {
    var el = $("status");
    el.textContent = text;
    el.className = "status" + (cls ? " " + cls : "");
  }

  function api(path) { return path + qs; }

  // ---------- WebSocket ----------
  // 每次连接/重连都重新取 /wsconfig：Win7 兼容通道可能稍后才启动成功，
  // 缓存一次性 Promise 会让 wsPort=0 的结果永久卡死（本页还有 3s HTTP 轮询兜底）。
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
    loadWsConfig().then(function (c) {
      if (!c) { scheduleReconnect(); return; }
      wsCfg = c;
      if (c.needCompat && !c.compat && c.error) {
        setStatus("兼容通道未就绪：" + c.error, "err");
      }
      var proto = location.protocol === "https:" ? "wss" : "ws";
      try { ws = new WebSocket(proto + "://" + wsHost() + api("/share/ws")); }
      catch (e) { scheduleReconnect(); return; }

      ws.onopen = function () { setStatus("已连接", "ok"); };
      ws.onclose = function () { setStatus("连接断开，重连中…", "err"); scheduleReconnect(); };
      ws.onerror = function () { setStatus("连接异常", "err"); };
      ws.onmessage = function (ev) {
        try {
          var msg = JSON.parse(ev.data);
          if (msg && msg.type === "state") { applyState(msg); }
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

  // 仅当 WebSocket 未连上时才用 HTTP 轮询兜底（Win7 等无 HTTP.sys WebSocket 的老系统）
  // 上一次请求未回来前不再发下一次，避免慢响应叠加把服务端压垮；同时加超时防止卡死在 in-flight。
  var polling = false;
  setInterval(function () {
    if (ws && ws.readyState === 1) return;
    if (polling) return;
    polling = true;
    var ctrl = (typeof AbortController !== "undefined") ? new AbortController() : null;
    var timer = ctrl ? setTimeout(function () { ctrl.abort(); }, 5000) : null;
    var done = function () { if (timer) clearTimeout(timer); polling = false; };
    fetch(api("/share/api/state"), { cache: "no-store", signal: ctrl ? ctrl.signal : undefined })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (s) { if (s) { applyState(s); setStatus("已连接 · 轮询", "ok"); } })
      .catch(function () {})
      .then(done, done);
  }, 3000);

  // ---------- 状态渲染 ----------
  function applyState(s) {
    state = s;
    state.pptItems = s.pptItems || [];
    renderMode();
    renderPptList();
    renderPreview();
    $("wbCount").textContent = (s.count > 0 ? (s.index + 1) : 0) + " / " + s.count;
  }

  function renderMode() {
    var badge = $("modeBadge"), note = $("modeNote");
    if (state.pptMode) {
      badge.textContent = "PPT 放映模式 · 第 " + state.pptSlide + " 页";
      badge.className = "badge ppt";
      note.textContent = "上传的图片直接叠加显示在当前屏幕上，不进入内置白板；换页自动隐藏，翻回来重新显示，并自动保存到墨迹的同一文件夹。";
    } else {
      badge.textContent = "内置白板模式";
      badge.className = "badge";
      note.textContent = "上传的图片会成为内置白板的一页（可切换白色底色），一页显示一张。";
    }
  }

  function renderPreview() {
    var cur = $("cur"), empty = $("empty");
    var img = (state.index >= 0 && state.index < localImages.length) ? localImages[state.index] : null;
    if (img) {
      cur.src = img.url; cur.style.display = "block"; empty.style.display = "none";
    } else {
      cur.style.display = "none"; empty.style.display = "block";
      empty.textContent = "尚未选择 / 上传图片";
    }
    var box = $("thumbs");
    box.innerHTML = "";
    localImages.forEach(function (im, i) {
      var el = document.createElement("img");
      el.src = im.url;
      if (i === state.index) el.className = "active";
      el.onclick = function () { send({ action: "goto", index: i }); };
      box.appendChild(el);
    });
  }

  function renderPptList() {
    var list = $("pptList");
    $("pptCount").textContent = state.pptItems.length + " 张";
    list.innerHTML = "";
    if (!state.pptItems.length) {
      var li = document.createElement("li");
      li.className = "empty";
      li.textContent = "暂无放映模式上传的图片。进入 PPT 放映后上传即可在这里逐张控制显隐。";
      list.appendChild(li);
      return;
    }
    state.pptItems.forEach(function (it) {
      var li = document.createElement("li");
      li.className = "item";

      var info = document.createElement("div");
      info.className = "info";
      var nm = document.createElement("div");
      nm.className = "nm";
      nm.textContent = it.name || ("图片 " + (it.index + 1));
      var meta = document.createElement("div");
      meta.className = "meta";
      meta.innerHTML = '<span class="tag">第 ' + it.slide + ' 页</span> · ' +
        (it.visible ? "已显示" : "已隐藏") + (it.slide === state.pptSlide ? "" : " · 不在当前页");
      info.appendChild(nm); info.appendChild(meta);
      li.appendChild(info);

      var toggle = document.createElement("button");
      toggle.className = "small " + (it.visible ? "on" : "off");
      toggle.textContent = it.visible ? "隐藏" : "显示";
      toggle.onclick = function () {
        var value = !it.visible;
        send({ action: "visible", index: it.index, value: value });
        // WS 不可达时走 HTTP 兜底
        fetch(api("/share/api/visible"), {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ index: it.index, value: value })
        }).then(function (r) { return r.ok ? r.json() : null; })
          .then(function (s) { if (s) applyState(s); }).catch(function () {});
      };

      var del = document.createElement("button");
      del.className = "small danger";
      del.textContent = "删除";
      del.onclick = function () { send({ action: "pptremove", index: it.index }); };

      li.appendChild(toggle); li.appendChild(del);
      list.appendChild(li);
    });
  }

  // ---------- 上传 ----------
  function fmtSize(n) {
    if (n >= 1048576) return (n / 1048576).toFixed(1) + " MB";
    if (n >= 1024) return (n / 1024).toFixed(0) + " KB";
    return n + " B";
  }
  function statusText(s) {
    return ({ queued: "排队中", uploading: "上传中…", done: "已完成", failed: "失败" })[s] || s;
  }
  function isImage(f) {
    if ((f.type || "").toLowerCase().indexOf("image/") === 0) return true;
    return /\.(jpe?g|png|gif|bmp|webp|heic|heif|tiff?)$/i.test(f.name || "");
  }

  function renderQueue() {
    var list = $("transferList");
    $("uploadCount").textContent = queue.length + " 个文件";
    list.innerHTML = "";
    queue.forEach(function (it) {
      var li = document.createElement("li");
      li.className = "item";
      var thumb = document.createElement("img");
      thumb.src = it.url;
      var info = document.createElement("div");
      info.className = "info";
      var nm = document.createElement("div"); nm.className = "nm"; nm.textContent = it.name;
      var meta = document.createElement("div"); meta.className = "meta";
      meta.textContent = fmtSize(it.size) + " · " + statusText(it.status);
      info.appendChild(nm); info.appendChild(meta);
      li.appendChild(thumb); li.appendChild(info);
      list.appendChild(li);
    });
  }

  function addToQueue(files) {
    for (var i = 0; i < files.length; i++) {
      var f = files[i];
      if (!isImage(f)) continue;
      queue.push({ file: f, name: f.name, size: f.size, url: URL.createObjectURL(f), status: "queued", serverObj: null });
    }
    renderQueue();
    startUpload();
  }

  function startUpload() {
    if (uploading) return;
    uploading = true; processQueue();
  }

  function processQueue() {
    var it = null;
    for (var i = 0; i < queue.length; i++) if (queue[i].status === "queued") { it = queue[i]; break; }
    if (!it) { uploading = false; return; }
    it.status = "uploading"; renderQueue();
    uploadOne(it).then(function (j) {
      if (j && j.ok) {
        it.status = "done";
        var obj = { name: it.name, url: it.url };
        it.serverObj = obj;
        // 白板图片本机留一份预览；放映模式图片由网页端控制显隐，不进预览列表
        if (!j.pptMode) localImages.push(obj);
        setStatus("已上传，共 " + (j.pptMode ? "放映图片" : j.count + " 张"), "ok");
      } else {
        it.status = "failed"; setStatus("上传失败", "err");
        // 失败项仍留在 queue 里由 renderQueue 显示缩略图，不能在此 revoke，
        // 否则缩略图变破图；页面卸载时统一释放。
      }
      renderQueue(); renderPreview(); processQueue();
    }).catch(function (e) {
      it.status = "failed"; setStatus("上传异常：" + (e && e.message || e), "err");
      renderQueue(); processQueue();
    });
  }

  // 客户端等比缩到 2000px 再上传（JPEG 0.85），避免手机原图过大导致超时
  function uploadOne(it) {
    return new Promise(function (resolve, reject) {
      var img = new Image();
      img.onerror = function () { reject(new Error("图片解码失败")); };
      img.onload = function () {
        try {
          var max = 2000, w = img.naturalWidth || 0, h = img.naturalHeight || 0;
          var scale = (w > 0 && h > 0) ? Math.min(1, max / Math.max(w, h)) : 1;
          var cv = document.createElement("canvas");
          cv.width = Math.max(1, Math.round(w * scale));
          cv.height = Math.max(1, Math.round(h * scale));
          cv.getContext("2d").drawImage(img, 0, 0, cv.width, cv.height);
          var base64 = String(cv.toDataURL("image/jpeg", 0.85)).split(",")[1] || "";
          var ctrl = (typeof AbortController !== "undefined") ? new AbortController() : null;
          var timer = ctrl ? setTimeout(function () { ctrl.abort(); }, 30000) : null;
          var clear = function () { if (timer) clearTimeout(timer); };
          fetch(api("/share/api/upload"), {
            method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ images: [{ name: it.name, data: base64 }] }),
            signal: ctrl ? ctrl.signal : undefined
          }).then(function (r) { return r.json(); })
            .then(function (v) { clear(); resolve(v); })
            .catch(function (e) { clear(); reject(e); });
        } catch (e) { reject(e); }
      };
      img.src = it.url;
    });
  }

  // ---------- 控件 ----------
  $("pick").addEventListener("click", function () { $("file").click(); });
  $("file").addEventListener("change", function (e) {
    if (e.target.files && e.target.files.length) addToQueue(e.target.files);
    e.target.value = "";
  });
  $("showAll").addEventListener("click", function () { send({ action: "showall" }); });
  $("hideAll").addEventListener("click", function () { send({ action: "hideall" }); });
  $("clearPpt").addEventListener("click", function () {
    if (confirm("确定清空全部放映模式上传的图片？对应的自动保存文件也会被删除。")) send({ action: "pptclear" });
  });

  var acts = {
    prev: function () { send({ action: "prev" }); },
    next: function () { send({ action: "next" }); },
    zoomin: function () { send({ action: "zoom", factor: 1.2 }); },
    zoomout: function () { send({ action: "zoom", factor: 1 / 1.2 }); },
    rotl: function () { send({ action: "rotate", delta: -15 }); },
    rotr: function () { send({ action: "rotate", delta: 15 }); },
    reset: function () { send({ action: "reset" }); }
  };
  Array.prototype.forEach.call(document.querySelectorAll("button[data-act]"), function (b) {
    var a = b.getAttribute("data-act");
    if (acts[a]) b.addEventListener("click", acts[a]);
  });

  var codeInput = $("code");
  codeInput.value = code;
  codeInput.addEventListener("change", function () {
    var v = codeInput.value.trim();
    location.href = "/share" + (v ? "?code=" + encodeURIComponent(v) : "");
  });

  // 页面卸载时归还所有预览/失败图片的 blob URL，避免浏览器进程内长期占用
  window.addEventListener("pagehide", function () {
    for (var i = 0; i < queue.length; i++) { try { URL.revokeObjectURL(queue[i].url); } catch (e) {} }
    // localImages 存的是 {name,url} 对象，必须取 .url
    for (var j = 0; j < localImages.length; j++) { try { URL.revokeObjectURL(localImages[j].url); } catch (e) {} }
  });

  renderMode(); renderPreview(); renderPptList(); renderQueue();
  connect();
})();
