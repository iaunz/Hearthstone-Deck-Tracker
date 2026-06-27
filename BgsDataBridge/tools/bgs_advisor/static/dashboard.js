"use strict";
// BgsViewer 主体包在 IIFE 里，避免顶层 let/const（如 `let n`）污染全局作用域、
// 与浏览器扩展或其它脚本的全局同名声明冲突（曾导致 SyntaxError: Identifier 'n' has already been declared）。
(function(){
const body = document.body;
const MODE = body.dataset.mode;                  // "live" | "replay"
const STATE_URL = body.dataset.stateUrl;
const STATE_POLL = body.dataset.statePoll === "true";

let events = [];          // 归一化事件数组
let lastSeq = 0;
let n = 0;                // 已应用的事件数（live=末尾；replay=拖动位置）
let playing = null;       // setInterval id

const KW = {TAUNT:"TAUNT",DIVINE_SHIELD:"DS",POISONOUS:"POIS",VENOMOUS:"VENM",
  REBORN:"REBORN",WINDFURY:"WIND",STEALTH:"STLTH",FROZEN:"FRZN"};

function isGolden(cardId){ return typeof cardId === "string" && cardId.endsWith("_G"); }

function minionCard(m, idx){
  const cardId = m.cardId || m.name || "?";
  const div = document.createElement("div");
  div.className = "card" + (isGolden(cardId) ? " gold" : "");
  const nm = m.name || cardId;
  const st = (m.attack!=null && m.health!=null) ? `<span class="st">${m.attack}/${m.health}</span>` : "";
  const kws = (m.keywords||[]).map(k=>`<span class="kw k-${k}">${KW[k]||k}</span>`).join(" ");
  div.innerHTML = `<div class="nm">${idx!=null?"["+idx+"] ":""}${nm}</div>${st} ${kws}`;
  if (m.text) div.title = m.text;
  div.style.cursor = "pointer";
  div.onclick = () => showJson(m);   // spec §8.4：点随从看原始 JSON
  return div;
}
function fillCards(elId, list){
  const el = document.getElementById(elId); el.innerHTML = "";
  (list||[]).forEach((m,i)=>el.appendChild(minionCard(m,i+1)));
  if (!(list&&list.length)) el.innerHTML = `<span style="color:var(--dim)">（空）</span>`;
}

function renderSnapshot(view){
  if (!view || !Object.keys(view).length){
    document.getElementById("board").innerHTML = `<span style="color:var(--dim)">等待事件…</span>`;
    return;
  }
  const match = view.match || {}, player = view.player || {}, shop = view.shop || null,
        lo = view.lastOpponent || null, lobby = view.lobby || null, hero = player.hero || {};
  // 顶栏
  const races = (view.availableRaces||[]).map(r=>`<span class="chip race">${r}</span>`).join(" ");
  const hpTxt = hero.health!=null ? `${hero.health}hp`+(hero.armor?` +${hero.armor}`:"") : "";
  const mmr = (match.rating||{}).mmr;
  document.getElementById("topbar").innerHTML =
    `<span class="hero-name">${hero.name||player.name||"?"}</span>
     <span class="chip">turn ${match.turn!=null?match.turn:"-"}</span>
     <span class="chip">phase ${match.phase||"-"}</span>
     <span class="chip">${match.gameType||""}</span>
     ${mmr!=null?`<span class="chip">MMR ${mmr}</span>`:""}
     ${races}
     <span id="state-badge" class="badge-off">state?</span>`;
  // 三列
  fillCards("board", player.board);
  document.getElementById("hero").innerHTML =
    `hero: ${hero.name||hero.cardId||"?"} ${hpTxt}` +
    (player.heroPower?` · heroPower: ${player.heroPower.name||player.heroPower.cardId||""}`:"");
  const tierStr = shop && shop.tier!=null ? `(tier ${shop.tier})` : `(tier ${player.tier!=null?player.tier:"-"})`;
  document.querySelector("#shop").previousElementSibling.textContent = "SHOP "+tierStr;
  fillCards("shop", shop?shop.offers:[]);
  fillCards("opponent", lo?lo.board:[]);
  document.getElementById("opponent-hero").innerHTML =
    lo?`turn ${lo.turn||"?"} · ${((lo.hero||{}).name)||((lo.hero||{}).cardId)||"?"}`:"（无）";
  // extras：饰品 / 任务 / 大厅
  const trinkets = (player.trinkets||[]).map(t=>`<span class="chip">trinket[${t.slot}] ${t.name||t.cardId||""}</span>`).join(" ");
  const q = player.questReward;
  const qStr = q ? `<span class="chip">quest: ${q.name||q.cardId} ${q.progress!=null?`(${q.progress}/${q.total||"?"})`:""}</span>` : "";
  const lobbyStr = (lobby&&lobby.players||[]).length
    ? `<details><summary>lobby (${lobby.players.length})</summary>${lobby.players.map(p=>`<div>${p.name} · ${p.heroCardId}</div>`).join("")}</details>` : "";
  document.getElementById("extras").innerHTML = trinkets + qStr + lobbyStr;
}

async function refreshView(){
  if (MODE === "live" && STATE_POLL){
    try{
      const st = await fetch(`${STATE_URL}/state?text=1`).then(r=>r.json());
      renderSnapshot(st);                       // /state 更新鲜，直接覆盖
      setBadge("state 在线");
      return;
    }catch(e){ setBadge("state 离线"); }
  }
  const v = await fetch(`/api/view?n=${n}`).then(r=>r.json());
  renderSnapshot(v);
}
function setBadge(txt){ const b=document.getElementById("state-badge"); if(b) b.textContent=txt; }

// —— 事件流 + 时间轴 ——
function eventTurn(e){
  return (e.match&&e.match.turn!=null?e.match.turn:
          (e.data&&e.data.turn!=null?e.data.turn:
          (e.data&&e.data.match&&e.data.match.turn)));
}
function renderEventStream(){
  const ul = document.getElementById("evlist"); ul.innerHTML="";
  events.forEach((e,i)=>{
    const li=document.createElement("li");
    li.textContent = `#${e.seq||"?"} ${e.type||"?"} t${eventTurn(e)!=null?eventTurn(e):"-"} ${e.at||""}`;
    if(i===n-1) li.style.background="#243049";
    li.onclick=()=>showJson(events[i]);   // spec §8.4：点事件看完整信封 JSON
    ul.appendChild(li);
  });
  ul.scrollTop = ul.scrollHeight;
}
function renderRounds(){
  const el=document.getElementById("rounds"); el.innerHTML="";
  const turns=[...new Set(events.map(eventTurn).filter(t=>t!=null))].sort((a,b)=>a-b);
  const curTurn = eventTurn(events[n-1]);
  turns.forEach(t=>{
    const d=document.createElement("span"); d.className="round"+(t===curTurn?" cur":"");
    d.textContent=`T${t}`;
    const firstIdx = events.findIndex(e=>eventTurn(e)===t);
    d.onclick=()=>{ n=firstIdx+1; syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
    el.appendChild(d);
  });
}

// —— 增量轮询（live）——
async function poll(){
  try{
    const got = await fetch(`/api/events?since=${lastSeq}`).then(r=>r.json());
    if(got.length){
      events = events.concat(got);
      lastSeq = events.length?Math.max(...events.map(e=>e.seq||0)):lastSeq;
      if(MODE==="live"){ n=events.length; }
      const max=events.length;
      const scrub=document.getElementById("scrub"); scrub.max=max;
      if(MODE==="live"){ scrub.value=max; }
      renderEventStream(); renderRounds();
    }
    refreshView();
  }catch(e){ setBadge("后端离线"); }
}

// —— 回放控制 ——
function syncScrub(){ document.getElementById("scrub").value=n;
  document.getElementById("pos").textContent=`${n}/${events.length}`; }
function initReplay(){
  if(MODE!=="replay") return;
  const c=document.getElementById("controls");
  const scrub=document.getElementById("scrub");
  scrub.oninput=()=>{ n=+scrub.value; syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
  document.getElementById("prev").onclick=()=>{ if(n>0){n--;} syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
  document.getElementById("next").onclick=()=>{ if(n<events.length){n++;} syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
  document.getElementById("play").onclick=function(){
    if(playing){ clearInterval(playing); playing=null; this.textContent="▶"; return; }
    this.textContent="⏸";
    playing=setInterval(()=>{
      if(n>=events.length){ clearInterval(playing); playing=null; document.getElementById("play").textContent="▶"; return; }
      n++; syncScrub(); refreshView(); renderEventStream(); renderRounds();
    }, +document.getElementById("speed").value);
  };
  c.style.display = "";
}

// —— 进度曲线 ——
let chartHp,chartAtk,chartTier;
function ensureCharts(){
  if(chartHp) return;
  const opt=(y)=>({responsive:true,maintainAspectRatio:false,plugins:{legend:{display:false}},
    scales:{y:{beginAtZero:true}}});
  chartHp=new Chart(document.getElementById("chartHp"),{type:"line",data:{labels:[],datasets:[{label:"hero hp",borderColor:"#e34c4c",data:[]}]},options:opt()});
  chartAtk=new Chart(document.getElementById("chartAtk"),{type:"line",data:{labels:[],datasets:[{label:"board atk",borderColor:"#5b9cff",data:[]}]},options:opt()});
  chartTier=new Chart(document.getElementById("chartTier"),{type:"bar",data:{labels:[],datasets:[{label:"tier",borderColor:"#4caf50",backgroundColor:"#4caf50",data:[]}]},options:opt()});
}
async function refreshProgression(){
  const prog = await fetch("/api/progression").then(r=>r.json());
  ensureCharts();
  const labels=prog.map(p=>"T"+p.turn);
  chartHp.data.labels=labels; chartHp.data.datasets[0].data=prog.map(p=>p.heroHp); chartHp.update();
  chartAtk.data.labels=labels; chartAtk.data.datasets[0].data=prog.map(p=>p.boardAtk); chartAtk.update();
  chartTier.data.labels=labels; chartTier.data.datasets[0].data=prog.map(p=>p.tier); chartTier.update();
}

// —— 详情抽屉 ——
function showJson(obj){
  const d=document.getElementById("drawer");
  document.getElementById("drawer-json").textContent=JSON.stringify(obj,null,2);
  d.classList.remove("hidden");
}
document.getElementById("drawer-close").onclick=()=>document.getElementById("drawer").classList.add("hidden");
document.getElementById("topbar").addEventListener("click",ev=>{
  if(ev.target.classList.contains("hero-name")) refreshView();
});

// —— 启动 ——
(async function init(){
  document.getElementById("controls").style.display = (MODE==="replay")?"":"none";
  // 首次拉全量
  events = await fetch(`/api/events?since=0`).then(r=>r.json());
  lastSeq = events.length?Math.max(...events.map(e=>e.seq||0)):0;
  n = (MODE==="live")?events.length:events.length;
  document.getElementById("scrub").max=events.length;
  if(MODE==="replay"){ document.getElementById("scrub").value=n; }
  syncScrub();
  renderEventStream(); renderRounds(); initReplay();
  await refreshView(); await refreshProgression();
  if(MODE==="live"){ setInterval(poll, 1000); setInterval(refreshProgression, 3000); }
})();
})(); // —— /BgsViewer scope ——

// ── BgsAdvisor:ADVICE 面板(自包含,独立 1s 轮询) ──────────────────────────
(function () {
  const KIND_LABEL = { BUY: "买", SELL: "卖", PLAY: "上场", REPOSITION: "调位",
    LEVEL_UP: "升本", REROLL: "刷新", FREEZE: "冻结", HERO_POWER: "技能",
    PLACE: "摆位", PICK_HERO: "选英雄", PICK_TRINKET: "选饰品" };
  let lastTs = 0;

  function postAdvise(dtype) {
    fetch("/api/advise", { method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ decisionType: dtype }) });
  }
  document.getElementById("advise-shop").onclick = () => postAdvise("ShopPhase");
  document.getElementById("advise-pos").onclick = () => postAdvise("Positioning");

  function render(a) {
    const status = document.getElementById("advice-status");
    const actions = document.getElementById("advice-actions");
    const rationale = document.getElementById("advice-rationale");
    const meta = document.getElementById("advice-meta");
    const freshness = document.getElementById("advice-freshness");
    if (!a) { status.textContent = "就绪"; actions.innerHTML = ""; rationale.textContent = ""; meta.textContent = ""; return; }
    status.textContent = { thinking: "思考中…", ok: "●就绪", error: "✕错误" }[a.status] || a.status;
    status.className = "advice-status " + a.status;
    if (a.status === "error") { actions.innerHTML = ""; rationale.textContent = "⚠️ 云端不可用,未生成建议"; }
    else if (a.status === "thinking") { actions.innerHTML = ""; rationale.textContent = ""; }
    else {
      actions.innerHTML = (a.actions || []).map(act => {
        const lbl = KIND_LABEL[act.kind] || act.kind;
        const name = act.name || act.cardId || "";
        const idx = act.index != null ? `→位${act.index}` : "";
        const note = act.note ? ` <em>${act.note}</em>` : "";
        return `<div class="action-card"><span class="kind">${lbl}</span> ${name}${idx}${note}</div>`;
      }).join("");
      rationale.textContent = a.rationale || "";
    }
    const llm = a.llm || {};
    meta.textContent = a.status === "ok" && llm.model
      ? `来源: ${llm.model} · ${llm.latencyMs || "?"}ms` : "";
    const turn = (a.snapshotRef || {}).turn;
    freshness.textContent = turn != null ? `基于第${turn}回合` : "";
  }

  async function poll() {
    try {
      const r = await fetch(`/api/advice?sinceTs=${lastTs}`);
      const list = await r.json();
      if (Array.isArray(list) && list.length) {
        list.forEach(a => { if (a.ts > lastTs) lastTs = a.ts; });
        render(list[list.length - 1]);
      } else if (list === null) {
        render(null);
      }
    } catch (e) { /* 静默重试 */ }
  }
  setInterval(poll, 1000); poll();

  // ── 设置 ─────────────────────────────────────────────────────────────
  async function loadConfig() {
    const cfg = await (await fetch("/api/config")).json();
    document.getElementById("cfg-model").value = cfg.model || "";
    document.getElementById("cfg-baseurl").value = cfg.baseUrl || "";
    document.getElementById("cfg-autopick").checked = !!cfg.autoTriggerPicks;
    document.getElementById("cfg-apikey").placeholder = cfg.hasApiKey ? "(已配置,留空不改)" : "sk-ant-...";
  }
  document.getElementById("cfg-save").onclick = async () => {
    const body = { model: document.getElementById("cfg-model").value,
      baseUrl: document.getElementById("cfg-baseurl").value,
      autoTriggerPicks: document.getElementById("cfg-autopick").checked };
    const key = document.getElementById("cfg-apikey").value;
    if (key) body.apiKey = key;
    await fetch("/api/config", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    document.getElementById("cfg-msg").textContent = "已保存";
    loadConfig();
  };
  loadConfig();
})();
