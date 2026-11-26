// 局域网文件传输 · 网页端文件管理器
(function () {
  "use strict";

  var code = new URLSearchParams(location.search).get("code") || "";
  var cur = "";
  var $ = function (id) { return document.getElementById(id); };
  var statusEl = $("status");

  function setStatus(t, c) {
    statusEl.textContent = t;
    statusEl.className = "status" + (c ? " " + c : "");
  }
  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (m) {
      return ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[m];
    });
  }

  function apiUrl(action, params) {
    var u = "/file/api/" + action + "?code=" + encodeURIComponent(code);
    if (params) for (var k in params) if (params.hasOwnProperty(k)) u += "&" + k + "=" + encodeURIComponent(params[k]);
    return u;
  }
  function ok(r) {
    if (!r.ok) throw new Error("HTTP " + r.status);
    return r;
  }

  function fmtSize(n) {
    if (n >= 1048576) return (n / 1048576).toFixed(1) + " MB";
    if (n >= 1024) return (n / 1024).toFixed(0) + " KB";
    return n + " B";
  }

  function load() {
    setStatus("加载中…");
    fetch(apiUrl("list", { path: cur }))
      .then(function (r) { return ok(r).json(); })
      .then(function (j) {
        if (j && j.items) {
          cur = j.path || "";
          renderPath();
          renderRows(j.items);
          setStatus("共 " + j.items.length + " 项");
        } else setStatus("加载失败", "err");
      })
      .catch(function (e) { setStatus("加载失败：" + (e && e.message || e), "err"); });
  }

  function renderPath() {
    var p = $("path");
    if (!cur) { p.textContent = "/"; return; }
    var segs = cur.split("/");
    var html = "<a data-i=\"\" href=\"#\">/</a>";
    var acc = "";
    for (var i = 0; i < segs.length; i++) {
      acc += (i ? "/" : "") + segs[i];
      html += "<a data-i=\"" + esc(acc) + "\" href=\"#\">" + esc(segs[i]) + "</a>/";
    }
    p.innerHTML = html;
    Array.prototype.forEach.call(p.querySelectorAll("a"), function (a) {
      a.onclick = function (ev) { ev.preventDefault(); cur = a.getAttribute("data-i"); load(); };
    });
  }

  function renderRows(items) {
    var tb = $("rows");
    tb.innerHTML = "";
    var empty = $("empty");
    if (!items.length) { empty.style.display = "block"; return; }
    empty.style.display = "none";
    items.forEach(function (it) {
      var tr = document.createElement("tr");
      var isImg = !it.isDir && /\.(png|jpe?g|gif|webp|bmp|svg)$/i.test(it.name);

      var tdName = document.createElement("td");
      var nm = document.createElement(it.isDir ? "a" : "span");
      nm.className = "name" + (it.isDir ? " dir" : "");
      nm.textContent = it.name;
      if (it.isDir) {
        nm.href = "#";
        nm.onclick = function (e) { e.preventDefault(); cur = (cur ? cur + "/" : "") + it.name; load(); };
      } else if (isImg) {
        nm.style.cursor = "pointer";
        nm.onclick = function () { preview(it); };
      }
      tdName.appendChild(nm);

      var tdSize = document.createElement("td");
      tdSize.textContent = it.isDir ? "—" : fmtSize(it.size);
      var tdTime = document.createElement("td");
      tdTime.textContent = (it.mtime || "").replace("T", " ").slice(0, 16);

      var tdAct = document.createElement("td");
      if (!it.isDir) {
        var dl = document.createElement("a");
        dl.className = "act";
        dl.textContent = "下载";
        dl.href = "/file/dl?code=" + encodeURIComponent(code) + "&path=" + encodeURIComponent((cur ? cur + "/" : "") + it.name) + "&inline=0";
        dl.setAttribute("download", "");
        tdAct.appendChild(dl);
        var pv = document.createElement("button");
        pv.className = "act";
        pv.textContent = "预览";
        pv.onclick = function () { preview(it); };
        tdAct.appendChild(pv);
      }
      var del = document.createElement("button");
      del.className = "act del";
      del.textContent = "删除";
      del.onclick = function () { if (confirm("确定删除 “" + it.name + "”？")) doDelete(it); };
      tdAct.appendChild(del);

      tr.appendChild(tdName); tr.appendChild(tdSize); tr.appendChild(tdTime); tr.appendChild(tdAct);
      tb.appendChild(tr);
    });
  }

  function doDelete(it) {
    fetch(apiUrl("delete", { path: cur, name: it.name }), { method: "POST" })
      .then(ok)
      .then(function () { load(); })
      .catch(function (e) { setStatus("删除失败：" + (e && e.message || e), "err"); });
  }

  function preview(it) {
    var full = (cur ? cur + "/" : "") + it.name;
    var url = "/file/dl?code=" + encodeURIComponent(code) + "&path=" + encodeURIComponent(full) + "&inline=1";
    var body = $("modal-body");
    body.innerHTML = "";
    $("modal-title").textContent = it.name;
    var ext = (it.name.split(".").pop() || "").toLowerCase();
    var textExt = ["txt", "md", "csv", "log", "json", "xml", "js", "css", "html", "ini", "conf", "yml", "yaml", "bat", "sh", "text"];

    if (["png", "jpg", "jpeg", "gif", "webp", "bmp", "svg"].indexOf(ext) >= 0) {
      var img = document.createElement("img");
      img.src = url; img.className = "prev-img";
      body.appendChild(img);
    } else if (ext === "pdf") {
      var f = document.createElement("iframe");
      f.src = url; f.className = "prev-frame";
      body.appendChild(f);
    } else if (textExt.indexOf(ext) >= 0) {
      fetch(url).then(ok).then(function (r) { return r.text(); }).then(function (t) {
        var pre = document.createElement("pre");
        pre.className = "prev-text";
        pre.textContent = t;   // 用 textContent 渲染纯文本，防止 XSS
        body.appendChild(pre);
      }).catch(function () { body.innerHTML = "<div class='err'>预览失败</div>"; });
    } else {
      body.innerHTML = "<div class='err'>该类型不支持在线预览，请下载查看。</div>" +
        "<p><a class='act' href='/file/dl?code=" + encodeURIComponent(code) + "&path=" + encodeURIComponent(full) + "&inline=0' download>下载 " + esc(it.name) + "</a></p>";
    }
    $("modal").classList.remove("hidden");
  }

  // ---------- 上传（按钮 / 拖拽，二者共用） ----------
  function uploadFiles(files) {
    if (!files || !files.length) return;
    var done = 0;
    setStatus("上传 0/" + files.length);
    Array.prototype.forEach.call(files, function (f) {
      var relName = (f._rel || "") + f.name;   // 拖拽文件夹时保留相对路径
      fetch(apiUrl("upload", { path: cur, name: relName }), { method: "POST", body: f })
        .then(ok)
        .then(function () {
          done++;
          setStatus("上传 " + done + "/" + files.length);
          if (done === files.length) load();
        })
        .catch(function (err) {
          setStatus("上传失败：" + (err && err.message || err), "err");
          done++;
          if (done === files.length) load();
        });
    });
  }

  $("pick").onclick = function () { $("file").click(); };
  $("file").onchange = function (e) {
    uploadFiles(e.target.files);
    e.target.value = "";
  };

  // ---------- 拖拽上传（支持文件与文件夹） ----------
  var dropEl = $("drop");
  var dragDepth = 0;
  function showDrop() { if (dropEl) dropEl.classList.remove("hidden"); }
  function hideDrop() { if (dropEl) dropEl.classList.add("hidden"); }

  window.addEventListener("dragenter", function (e) { e.preventDefault(); dragDepth++; showDrop(); });
  window.addEventListener("dragover", function (e) { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = "copy"; });
  window.addEventListener("dragleave", function (e) { dragDepth--; if (dragDepth <= 0) { dragDepth = 0; hideDrop(); } });
  window.addEventListener("drop", function (e) {
    e.preventDefault();
    dragDepth = 0; hideDrop();
    var dt = e.dataTransfer;
    if (!dt) return;
    if (dt.items && dt.items.length && dt.items[0].webkitGetAsEntry) {
      handleEntries(dt.items);   // 同时覆盖“文件”与“文件夹”（用 webkitGetAsEntry 读取）
    } else if (dt.files && dt.files.length) {
      uploadFiles(dt.files);
    }
  });

  // 递归读取拖入的文件夹（webkitGetAsEntry），保留相对路径（f._rel）
  function readEntry(entry, prefix, out) {
    return new Promise(function (resolve) {
      if (entry.isFile) {
        entry.file(function (file) {
          try { file._rel = prefix; } catch (ex) { }
          out.push(file); resolve();
        }, function () { resolve(); });
      } else if (entry.isDirectory) {
        var reader = entry.createReader();
        var batch = [];
        (function readBatch() {
          reader.readEntries(function (entries) {
            if (!entries.length) {
              var proms = [];
              batch.forEach(function (en) { proms.push(readEntry(en, prefix + entry.name + "/", out)); });
              Promise.all(proms).then(resolve, resolve);
            } else {
              batch = batch.concat(Array.prototype.slice.call(entries));
              readBatch();
            }
          }, function () { resolve(); });
        })();
      } else resolve();
    });
  }

  function handleEntries(items) {
    var entries = [];
    for (var i = 0; i < items.length; i++) {
      var it = items[i];
      if (it.kind === "file") {
        var en = it.webkitGetAsEntry ? it.webkitGetAsEntry() : null;
        if (en) entries.push(en);
        else if (it.getAsFile) { var f = it.getAsFile(); if (f) uploadFiles([f]); }
      }
    }
    if (!entries.length) return;
    setStatus("读取拖拽内容…");
    var out = [];
    Promise.all(entries.map(function (en) { return readEntry(en, "", out); }))
      .then(function () { uploadFiles(out); })
      .catch(function () { hideDrop(); });
  }

  // ---------- 新建文件夹 / 上级 ----------
  $("mkdir").onclick = function () {
    var n = prompt("新文件夹名称：");
    if (!n) return;
    fetch(apiUrl("mkdir", { path: cur, name: n }), { method: "POST" })
      .then(ok)
      .then(function () { load(); })
      .catch(function (e) { setStatus("新建失败：" + (e && e.message || e), "err"); });
  };
  $("up").onclick = function () {
    if (!cur) return;
    var i = cur.lastIndexOf("/");
    cur = i < 0 ? "" : cur.substring(0, i);
    load();
  };

  // ---------- 模态关闭 ----------
  $("modal-close").onclick = function () { $("modal").classList.add("hidden"); };
  $("modal").onclick = function (e) { if (e.target === $("modal")) $("modal").classList.add("hidden"); };

  load();
})();
