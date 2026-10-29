namespace Bot.App;

/// <summary>本地配置页。全部资源内联，不依赖网络，也不监听外网地址。</summary>
public static class WebPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>聊天机器人</title>
<style>
  :root{--bg:#0f1216;--panel:#171c22;--line:#252c35;--text:#e6e9ee;--dim:#8b97a6;--accent:#4c8dff;--ok:#3ecf8e;--warn:#e8b04b;--err:#ef5f5f}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font:14px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}
  header{display:flex;align-items:center;gap:12px;padding:14px 20px;border-bottom:1px solid var(--line);background:var(--panel)}
  header h1{font-size:16px;margin:0;font-weight:600}
  .dot{width:9px;height:9px;border-radius:50%;background:var(--dim)}
  .dot.on{background:var(--ok)}.dot.warn{background:var(--warn)}.dot.err{background:var(--err)}
  #statusText{color:var(--dim);font-size:13px}
  nav{display:flex;gap:6px;padding:10px 20px 0}
  nav button{background:transparent;border:1px solid var(--line);color:var(--dim);padding:7px 14px;border-radius:8px 8px 0 0;cursor:pointer;font-size:13px}
  nav button.active{color:var(--text);border-bottom-color:var(--panel);background:var(--panel)}
  main{padding:16px 20px 40px;max-width:1080px}
  section{display:none}section.active{display:block}
  .card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:16px;margin-bottom:14px}
  .card h2{font-size:14px;margin:0 0 12px;color:var(--dim);font-weight:600;letter-spacing:.4px}
  label{display:block;margin:10px 0 4px;color:var(--dim);font-size:12px}
  input,select{width:100%;background:#0d1116;border:1px solid var(--line);color:var(--text);border-radius:7px;padding:8px 10px;font:inherit;font-size:13px}
  input[type=checkbox]{width:auto}
  .row{display:flex;gap:14px;flex-wrap:wrap}.row>div{flex:1;min-width:220px}
  .hint{color:var(--dim);font-size:12px;margin-top:4px}
  button.primary{background:var(--accent);border:none;color:#fff;padding:9px 18px;border-radius:8px;cursor:pointer;font-size:13px}
  button.ghost{background:transparent;border:1px solid var(--line);color:var(--text);padding:8px 14px;border-radius:8px;cursor:pointer;font-size:13px}
  table{width:100%;border-collapse:collapse;font-size:12.5px}
  th,td{text-align:left;padding:7px 8px;border-bottom:1px solid var(--line);vertical-align:top}
  th{color:var(--dim);font-weight:500}
  code{font-family:Consolas,monospace;font-size:12px;color:#9fc7ff}
  .tag{display:inline-block;padding:1px 7px;border-radius:99px;background:#1f2a36;color:var(--dim);font-size:11px}
  #chatlog{height:280px;overflow:auto;background:#0d1116;border:1px solid var(--line);border-radius:8px;padding:10px;font-size:13px}
  .msg{margin:6px 0}.msg .who{color:var(--dim);font-size:11px;margin-right:6px}
  .msg.me .who{color:#7fb2ff}
  .toast{position:fixed;right:18px;bottom:18px;background:#1f2a36;border:1px solid var(--line);padding:10px 16px;border-radius:8px;opacity:0;transition:.2s}
  .toast.show{opacity:1}
</style>
</head>
<body>
<header>
  <span class="dot" id="dot"></span>
  <h1>聊天机器人</h1>
  <span id="statusText">读取中…</span>
  <span style="flex:1"></span>
  <button class="ghost" onclick="loadStatus()">刷新</button>
</header>
<nav>
  <button data-tab="connect" class="active">连接</button>
  <button data-tab="tuning">调参</button>
  <button data-tab="memory">记忆</button>
  <button data-tab="sim">本地试聊</button>
</nav>
<main>
  <section id="tab-connect" class="active">
    <div class="card">
      <h2>模型</h2>
      <label>API Key</label>
      <input id="llmKey" type="password" placeholder="留空表示不修改">
      <div style="display:flex;gap:8px;margin-top:8px;align-items:center">
        <button class="ghost" onclick="testConn()">测试连接</button>
        <span class="hint" id="testHint" style="margin:0"></span>
      </div>
      <div class="hint" id="llmKeyHint"></div>
      <div class="row">
        <div><label>Base URL</label><input id="llmBase"></div>
        <div><label>模型 id</label><input id="model"></div>
      </div>
      <div class="row">
        <div><label>超时（秒）</label><input id="llmTimeout" type="number"></div>
        <div><label>最大输出 token</label><input id="llmMaxOut" type="number"></div>
      </div>
    </div>
    <div class="card">
      <h2>QQ 接入</h2>
      <label>适配器</label>
      <select id="adapter"><option value="mock">Mock（本地模拟，无需凭据）</option><option value="qq">QQ 官方机器人</option></select>
      <div class="row">
        <div><label>AppID</label><input id="qqAppId"></div>
        <div><label>AppSecret</label><input id="qqSecret" type="password" placeholder="留空表示不修改"></div>
      </div>
      <div class="hint">群主开启「允许机器人接收群内所有消息」后，平台会推 <code>GROUP_MESSAGE_CREATE</code>，
      程序收到第一条就会自动打开"能旁听全群"，不需要你手动勾。这个开关只是给"暂时收不到该事件"时的手动兜底。</div>
      <label style="display:flex;gap:8px;align-items:center;margin-top:10px">
        <input type="checkbox" id="qqAll"> 群主已开启「接收群内所有消息」
      </label>
    </div>
    <div class="card">
      <h2>人设</h2>
      <div class="row">
        <div><label>机器人名字</label><input id="botName"></div>
        <div><label>预设</label><select id="preset"><option value="economy">省流</option><option value="balanced">平衡</option><option value="experience">体验</option></select></div>
      </div>
      <label>人设描述</label><textarea id="persona" rows="3" style="width:100%;background:#0d1116;border:1px solid var(--line);color:var(--text);border-radius:7px;padding:8px 10px;font:inherit"></textarea>
    </div>
    <button class="primary" onclick="saveConfig()">保存并生效</button>
    <div class="hint" id="saveHint"></div>
  </section>

  <section id="tab-tuning">
    <div class="card">
      <h2>参数（改动「破坏缓存」项后会在下次空闲时自动重建会话）</h2>
      <div id="params"></div>
    </div>
    <button class="primary" onclick="saveParams()">保存参数</button>
  </section>

  <section id="tab-memory">
    <div class="card">
      <h2>人物</h2>
      <table><thead><tr><th>ID</th><th>名字</th><th>性格</th><th>评价</th><th>记忆</th><th>发言</th><th></th></tr></thead><tbody id="persons"></tbody></table>
    </div>
    <div class="card">
      <h2>事件</h2>
      <div style="display:flex;gap:8px;margin-bottom:10px"><input id="eventQuery" placeholder="搜索详情…"><button class="ghost" onclick="loadEvents()">搜索</button></div>
      <table><thead><tr><th>ID</th><th>时间</th><th>详情</th><th>状态</th><th>注入</th></tr></thead><tbody id="events"></tbody></table>
    </div>
    <div class="card">
      <h2>回滚</h2>
      <div style="display:flex;gap:8px"><input id="batchId" placeholder="batch_id"><button class="ghost" onclick="rollback()">按批次回滚</button></div>
    </div>
  </section>

  <section id="tab-sim">
    <div class="card">
      <h2>本地试聊（仅 Mock 适配器可用）</h2>
      <div id="chatlog"></div>
      <div style="display:flex;gap:8px;margin-top:10px">
        <input id="simInput" placeholder="输入一句话，回车发送">
        <button class="primary" onclick="simulate()">发送</button>
      </div>
      <div class="hint">这条链路走的是真实运行时：触发决策、三区组装、记忆检索、气泡切分都会执行，只有 QQ 发送是假的。</div>
    </div>
    <div class="card">
      <h2>当前会话</h2>
      <table><thead><tr><th>会话</th><th>状态</th><th>轮次</th><th>待回</th><th>前缀哈希</th></tr></thead><tbody id="sessions"></tbody></table>
    </div>
  </section>
</main>
<div class="toast" id="toast"></div>
<script>
let cfg = null, params = [];

function toast(msg){const t=document.getElementById('toast');t.textContent=msg;t.classList.add('show');setTimeout(()=>t.classList.remove('show'),2200);}
function api(path, opts){return fetch(path, opts).then(async r=>{const t=await r.text();let j=null;try{j=JSON.parse(t)}catch{} if(!r.ok) throw new Error((j&&j.error)||t);return j;});}

document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>{
  document.querySelectorAll('nav button').forEach(x=>x.classList.remove('active'));
  document.querySelectorAll('section').forEach(x=>x.classList.remove('active'));
  b.classList.add('active');
  document.getElementById('tab-'+b.dataset.tab).classList.add('active');
  if(b.dataset.tab==='memory'){loadPersons();loadEvents();}
  if(b.dataset.tab==='tuning')loadParams();
  if(b.dataset.tab==='sim'){loadStatus();}
});

async function loadStatus(){
  try{
    const s = await api('/api/status');
    const dot=document.getElementById('dot');
    dot.className='dot '+(s.adapter==='qq'?(s.connected?'on':'err'):'warn');
    document.getElementById('statusText').textContent =
      `${s.adapter==='qq'?'QQ 官方':s.adapter==='mock'?'Mock 模拟':s.adapter} · ${s.persons} 人 / ${s.events} 事件 · 今日 ${s.todayTokens} token`;
    const tb=document.getElementById('sessions'); tb.innerHTML='';
    (s.chats||[]).forEach(c=>{
      tb.innerHTML += `<tr><td><code>${esc(c.chatId)}</code></td><td><span class="tag">${c.state}</span></td><td>${c.turnCount}</td><td>${c.pendingCount}</td><td><code>${short(c.prefixHash)}</code></td></tr>`;
    });
    if(!(s.chats||[]).length) tb.innerHTML='<tr><td colspan="5" style="color:var(--dim)">还没有会话</td></tr>';
    if(s.warnings && s.warnings.length) document.getElementById('statusText').textContent += ' · ' + s.warnings.length + ' 条配置告警';
  }catch(e){toast('状态读取失败：'+e.message);}
}

async function testConn(){
  const hint=document.getElementById('testHint');
  hint.textContent='测试中…';
  try{
    const r=await api('/api/test',{method:'POST'});
    hint.textContent = r.ok
      ? `连通 OK · ${r.model} · ${r.latencyMs}ms · 命中 ${r.usage.hit} / 未命中 ${r.usage.miss}`
      : ('失败：' + (r.error||'未知错误'));
  }catch(e){ hint.textContent='失败：'+e.message; }
}

async function loadConfig(){
  cfg = await api('/api/config');
  document.getElementById('llmKeyHint').textContent = cfg.llm.apiKeySet ? '已设置（留空不修改）' : '未设置';
  document.getElementById('llmBase').value = cfg.llm.baseUrl||'';
  document.getElementById('model').value = cfg.parameters?.model || 'deepseek-chat';
  document.getElementById('llmTimeout').value = cfg.llm.timeoutSeconds;
  document.getElementById('llmMaxOut').value = cfg.llm.maxOutputTokens;
  document.getElementById('adapter').value = cfg.qq.adapter;
  document.getElementById('qqAppId').value = cfg.qq.appId||'';
  document.getElementById('qqAll').checked = !!cfg.qq.receivesAllMessages;
  document.getElementById('botName').value = cfg.persona.botName||'';
  document.getElementById('persona').value = cfg.persona.persona||'';
  document.getElementById('preset').value = cfg.preset||'balanced';
}

async function saveConfig(){
  const body = JSON.parse(JSON.stringify(cfg));
  body.llm.apiKey = document.getElementById('llmKey').value;
  body.llm.baseUrl = document.getElementById('llmBase').value;
  body.llm.timeoutSeconds = +document.getElementById('llmTimeout').value;
  body.llm.maxOutputTokens = +document.getElementById('llmMaxOut').value;
  body.qq.adapter = document.getElementById('adapter').value;
  body.qq.appId = document.getElementById('qqAppId').value;
  body.qq.receivesAllMessages = document.getElementById('qqAll').checked;
  const secret = document.getElementById('qqSecret').value;
  if(secret) body.qq.secret = secret;
  body.persona.botName = document.getElementById('botName').value;
  body.persona.persona = document.getElementById('persona').value;
  body.preset = document.getElementById('preset').value;
  body.parameters = body.parameters||{};
  body.parameters.model = document.getElementById('model').value;
  try{
    const r = await api('/api/config',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    document.getElementById('saveHint').textContent = '已保存' + ((r.warnings&&r.warnings.length)?('；告警：'+r.warnings.join('；')):'');
    document.getElementById('llmKey').value='';document.getElementById('qqSecret').value='';
    await loadConfig();
  }catch(e){toast('保存失败：'+e.message);}
}

async function loadParams(){
  params = await api('/api/params');
  const box=document.getElementById('params'); box.innerHTML='';
  const groups={};
  params.forEach(p=>{(groups[p.category]=groups[p.category]||[]).push(p)});
  for(const [cat,list] of Object.entries(groups)){
    let html=`<h3 style="font-size:13px;color:var(--accent);margin:16px 0 6px">${cat}</h3><div class="row">`;
    list.forEach(p=>{
      html+=`<div style="min-width:240px"><label>${p.name}${p.breaksCache?' <span class="tag">破坏缓存</span>':''}</label>`;
      if(p.type==='bool') html+=`<input type="checkbox" data-param="${p.name}" ${p.value?'checked':''}>`;
      else if(p.choices&&p.choices.length) html+=`<select data-param="${p.name}">${p.choices.map(c=>`<option ${c===p.value?'selected':''}>${c}</option>`).join('')}</select>`;
      else if(p.type==='list') html+=`<input data-param="${p.name}" value="${(p.value||[]).join(',')}">`;
      else html+=`<input data-param="${p.name}" value="${p.value}">`;
      html+=`<div class="hint">${p.description}${p.min!==null?`（${p.min}–${p.max}）`:''}</div></div>`;
    });
    box.innerHTML+=html+'</div>';
  }
}

async function saveParams(){
  const values={};
  document.querySelectorAll('#params [data-param]').forEach(el=>{
    const name=el.dataset.param;
    if(el.type==='checkbox') values[name]=el.checked;
    else if(el.tagName==='SELECT') values[name]=el.value;
    else {
      const spec=params.find(p=>p.name===name);
      if(spec.type==='int'||spec.type==='double') values[name]=Number(el.value);
      else if(spec.type==='list') values[name]=el.value.split(/[,，;；]/).map(s=>s.trim()).filter(Boolean);
      else values[name]=el.value;
    }
  });
  try{
    const r=await api('/api/params',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(values)});
    toast('参数已保存' + ((r.warnings&&r.warnings.length)?('，'+r.warnings.length+' 条告警'):''));
    await loadParams();
  }catch(e){toast('保存失败：'+e.message);}
}

async function loadPersons(){
  const rows=await api('/api/persons');
  const tb=document.getElementById('persons'); tb.innerHTML='';
  rows.forEach(p=>{
    tb.innerHTML+=`<tr><td><code>P${String(p.personId).padStart(3,'0')}</code></td><td>${esc(p.name)||'—'}</td><td>${esc(p.personality)||'—'}</td><td>${esc(p.evaluation)||'—'}</td><td>${esc(p.memoryText)||'—'}</td><td>${p.msgCount}</td>
    <td><button class="ghost" style="padding:2px 8px" onclick="forget('${p.openId}')">忘记</button></td></tr>`;
  });
  if(!rows.length) tb.innerHTML='<tr><td colspan="7" style="color:var(--dim)">还没有人物记忆</td></tr>';
}

async function loadEvents(){
  const q=document.getElementById('eventQuery').value||'';
  const rows=await api('/api/events?q='+encodeURIComponent(q));
  const tb=document.getElementById('events'); tb.innerHTML='';
  rows.forEach(e=>{
    tb.innerHTML+=`<tr><td><code>E${String(e.eventId).padStart(3,'0')}</code></td><td>${esc(e.timeText)||'—'}</td><td>${esc(e.detail)}</td><td>${e.status}</td><td>${e.injectedCount}</td></tr>`;
  });
  if(!rows.length) tb.innerHTML='<tr><td colspan="5" style="color:var(--dim)">没有事件</td></tr>';
}

async function forget(openId){
  if(!confirm('删除这个人的全部记忆？')) return;
  await api('/api/memory/forget',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({openId})});
  loadPersons();
}

async function rollback(){
  const batchId=document.getElementById('batchId').value.trim();
  if(!batchId) return;
  try{const r=await api('/api/memory/rollback',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({batchId})});
    toast('已回滚 '+r.reverted+' 条');}catch(e){toast('回滚失败：'+e.message);}
}

async function simulate(){
  const input=document.getElementById('simInput');
  const text=input.value.trim(); if(!text) return;
  input.value='';
  const log=document.getElementById('chatlog');
  log.innerHTML+=`<div class="msg me"><span class="who">我</span>${esc(text)}</div>`;
  try{
    const r=await api('/api/simulate',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({content:text})});
    (r.replies||[]).forEach(x=>{log.innerHTML+=`<div class="msg"><span class="who">${esc(x.label)}</span>${esc(x.content)}</div>`});
    if(!(r.replies||[]).length) log.innerHTML+=`<div class="msg"><span class="who">系统</span><span style="color:var(--dim)">未触发回复：${esc(r.reason||'')}</span></div>`;
    log.scrollTop=log.scrollHeight;
  }catch(e){log.innerHTML+=`<div class="msg"><span class="who">系统</span>${esc(e.message)}</div>`;}
}

document.getElementById('simInput').addEventListener('keydown',e=>{if(e.key==='Enter')simulate();});
function esc(s){return (s==null?'':String(s)).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
function short(h){return h?h.slice(0,14)+'…':'—';}
loadConfig();loadStatus();setInterval(loadStatus,5000);
</script>
</body>
</html>
""";
}
