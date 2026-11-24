"use strict";
const el = id => document.getElementById(id);
const text = (id, value) => { el(id).textContent = value ?? "—"; };
const fmt = (value, digits = 0) => Number.isFinite(value)
    ? value.toLocaleString("vi-VN", { maximumFractionDigits: digits }) : "—";
const clock = ms => new Date(ms).toLocaleTimeString("vi-VN", { hour12: false });
const scaled = (value, base) => Number.isFinite(value) ? value / base : null;
const historyLimit = 120;
let runId = null, latest = null, lastNewSample = 0;
const history = [];
function status(state, label) {
    el("status").dataset.state = state;
    text("status", label);
}
function note(stamp, message) {
    const li = document.createElement("li"), time = document.createElement("time");
    time.textContent = clock(stamp);
    li.append(time, document.createTextNode(message));
    const list = el("changes");
    list.prepend(li);
    while (list.children.length > 20) list.lastElementChild.remove();
}
function observed(oldSample, sample) {
    if (!oldSample) return;
    const oldRooms = new Map(oldSample.rooms.map(r => [r.id, r]));
    for (const room of sample.rooms) {
        const old = oldRooms.get(room.id);
        if (!old || old.phase !== room.phase || old.roundId !== room.roundId)
            note(sample.sampleUtc, "Room " + room.id + " · " + room.phase + " · round " + room.roundId);
        oldRooms.delete(room.id);
    }
    for (const id of oldRooms.keys()) note(sample.sampleUtc, "Room " + id + " đã đóng");
    if (oldSample.metrics.connectedClients !== sample.metrics.connectedClients)
        note(sample.sampleUtc, "Client kết nối: " + sample.metrics.connectedClients);
    if (oldSample.metrics.totals?.tcpSendErrors !== sample.metrics.totals?.tcpSendErrors)
        note(sample.sampleUtc, "Tổng lỗi TCP write: " + fmt(sample.metrics.totals?.tcpSendErrors));
}
function renderRooms(rooms, id) {
    const body = el("rooms");
    body.replaceChildren();
    for (const room of rooms ?? []) {
        const row = document.createElement("tr");
        for (const value of [room.id, room.roundId, room.phase, room.members, room.readyClients, room.activePlayers, room.alivePlayers]) {
            const cell = document.createElement("td");
            cell.textContent = value ?? "—";
            if (value === "Playing") cell.className = "phase";
            row.append(cell);
        }
        body.append(row);
    }
    el("empty").hidden = !!rooms?.length;
    text("empty", rooms ? "Hiện chưa có phòng." : "Đang chờ snapshot đầu tiên.");
    text("roomSample", id == null ? "—" : "Mẫu #" + id);
}
function diagnostics(m, d) {
    const values = [
        ["Action chờ", m?.waitingActions], ["Batch lớn nhất", m?.largestActionBatch],
        ["TCP FIFO chờ", m?.transport?.pending], ["TCP enqueue peak¹", m?.transport?.highWater],
        ["TCP write in-flight", m?.transport?.inFlight], ["Threads", m?.resources?.threads],
        ["TCP frames RX / TX", fmt(m?.events?.TcpFrameReceived) + " / " + fmt(m?.events?.TcpFrameWritten)],
        ["UDP datagrams RX / TX", fmt(m?.events?.UdpDatagramReceived) + " / " + fmt(m?.events?.UdpDatagramSent)],
        ["Lỗi gửi TCP / UDP", fmt(m?.totals?.tcpSendErrors) + " / " + fmt(m?.totals?.udpSendErrors)],
        ["Decode lỗi TCP / UDP", fmt(m?.events?.TcpMalformed) + " / " + fmt(m?.events?.UdpMalformed)],
        ["CSV · mẫu cuối", d?.lastCsvSampleId], ["Exporter bỏ mẫu", d?.droppedSnapshots],
        ["CSV", d?.csvFailed ? "Lỗi ghi" : d?.lastCsvSampleId > 0 ? "Đang ghi" : "Đang chờ"],
        ["Capture", d?.captureFailed ? "Có lỗi" : m ? "Có mẫu" : "Đang chờ"]
    ];
    const fragment = document.createDocumentFragment();
    for (const [name, value] of values) {
        const div = document.createElement("div"), term = document.createElement("dt"), definition = document.createElement("dd");
        term.textContent = name.replace("¹", "");
        if (name.endsWith("¹")) term.title = "High-water lớn nhất của các TCP wrapper hiện tại";
        definition.textContent = value ?? "—";
        div.append(term, definition);
        fragment.append(div);
    }
    el("diagnostics").replaceChildren(fragment);
}
function cards(m) {
    for (const [id, value] of Object.entries({ connected: m?.connectedClients, ready: m?.readyClients,
        roomCount: m?.roomCount, playing: m?.playingRooms, active: m?.activePlayers, alive: m?.alivePlayers,
        hz: fmt(m?.tickRate, 2), rtt: fmt(m?.transport?.heartbeatMeanMs, 1), rttN: m?.transport?.heartbeatSamples,
        cpuNow: fmt(m?.cpuPercent, 2), ramNow: fmt(scaled(m?.workingSetBytes, 1048576), 1) })) text(id, value);
}
const charts = [
    ["tickChart", [["intervalMs", "#74e6b4"]], 33.333],
    ["netChart", [["tcpRx", "#69b5ff"], ["tcpTx", "#69b5ff", true], ["udpRx", "#ffb477"], ["udpTx", "#ffb477", true]]],
    ["cpuChart", [["cpu", "#bba5ff"]]], ["ramChart", [["ram", "#73dfe9"]]]
];
function draw([id, series, reference]) {
    const canvas = el(id), ctx = canvas.getContext("2d");
    const w = canvas.clientWidth, h = canvas.clientHeight, dpi = window.devicePixelRatio || 1;
    if (!w || !h) return;
    if (canvas.width !== Math.round(w*dpi) || canvas.height !== Math.round(h*dpi)) {
        canvas.width = Math.round(w*dpi); canvas.height = Math.round(h*dpi);
    }
    ctx.setTransform(dpi,0,0,dpi,0,0);
    ctx.clearRect(0,0,w,h);
    const top=9, bottom=h-21, left=38, right=w-6;
    const ceiling=Math.max(1,reference || 0,...history.flatMap(p => series.map(([key]) => p[key] || 0)))*1.08;
    const y = value => bottom - (bottom-top)*value/ceiling;
    ctx.font="10px Segoe UI, sans-serif"; ctx.textAlign="right"; ctx.fillStyle="#a2b2c5";
    for (const f of [0,.5,1]) {
        const py=y(f*ceiling); ctx.fillText(fmt(f*ceiling,ceiling<10?1:0),left-5,py+3);
        ctx.beginPath();ctx.strokeStyle="#2b3848";ctx.lineWidth=1;ctx.setLineDash([]);ctx.moveTo(left,py);ctx.lineTo(right,py);ctx.stroke();
    }
    if (reference) {
        ctx.beginPath();ctx.strokeStyle="#a2b2c5";ctx.setLineDash([3,4]);ctx.moveTo(left,y(reference));ctx.lineTo(right,y(reference));ctx.stroke();
    }
    if (!history.length) return;
    const first=history[0].t, last=history[history.length-1].t;
    const x = t => left+(t-first)*(right-left)/Math.max(1000,last-first);
    for (const [key,color,dash] of series) {
        ctx.beginPath();ctx.strokeStyle=color;ctx.lineWidth=1.8;ctx.setLineDash(dash?[5,3]:[]);
        let previous;
        for (const p of history) {
            if (!Number.isFinite(p[key])) { previous=null;continue; }
            if (!previous || p.id!==previous.id+1 || p.t-previous.t>3000) ctx.moveTo(x(p.t),y(p[key]));
            else ctx.lineTo(x(p.t),y(p[key]));
            previous=p;
        }
        ctx.stroke();
    }
    ctx.textAlign="left";ctx.fillText(clock(first),left,h-3);
    if (last>first) {ctx.textAlign="right";ctx.fillText(clock(last),right,h-3);}
}
function redraw() { charts.forEach(draw); }
function apply(sample) {
    if (runId !== sample.runId) {
        runId = sample.runId;latest=null;history.length=0;el("changes").replaceChildren();
        cards(null);renderRooms(null,null);text("lastTime","Chưa có snapshot");text("sample",null);redraw();
    }
    const seconds=Math.floor(sample.uptimeSeconds);
    text("uptime",[Math.floor(seconds/3600),Math.floor(seconds/60)%60,seconds%60].map(n=>String(n).padStart(2,"0")).join(":"));
    const m=sample.metrics;
    diagnostics(m,sample.diagnostics);
    if (!m) {status("starting","Đang khởi động");return;}
    if (!latest || sample.sampleId>latest.sampleId) {
        observed(latest,sample);latest=sample;lastNewSample=performance.now();
        const n=m.network;
        history.push({ id:sample.sampleId,t:Date.parse(sample.sampleUtc),intervalMs:m.intervalMs,cpu:m.cpuPercent,
            ram:scaled(m.workingSetBytes,1048576),tcpRx:scaled(n?.tcpReceivedBytesPerSecond,1024),
            tcpTx:scaled(n?.tcpCompletedWriteBytesPerSecond,1024),udpRx:scaled(n?.udpReceivedBytesPerSecond,1024),
            udpTx:scaled(n?.udpCompletedSendBytesPerSecond,1024) });
        if (history.length>historyLimit) history.shift();
        cards(m);renderRooms(sample.rooms,sample.sampleId);text("sample","#"+sample.sampleId);
        text("lastTime","Snapshot · "+clock(sample.sampleUtc));redraw();
    }
    if (sample.diagnostics?.captureFailed || performance.now()-lastNewSample>3200) status("stale","Mẫu chậm");
    else status("live","Trực tuyến");
}
async function poll() {
    const started=performance.now(), controller=new AbortController();
    const timer=setTimeout(()=>controller.abort(),2500);
    try {
        const response=await fetch("/api/monitor",{cache:"no-store",signal:controller.signal});
        if (!response.ok) throw Error("HTTP "+response.status);
        apply(await response.json());
    } catch { status("disconnected","Mất kết nối"); }
    finally {clearTimeout(timer);setTimeout(poll,Math.max(0,1000-(performance.now()-started)));}
}
window.addEventListener("resize",redraw);
poll();
