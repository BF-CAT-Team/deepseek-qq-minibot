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
  input,select,textarea{width:100%;background:#0d1116;border:1px solid var(--line);color:var(--text);border-radius:7px;padding:8px 10px;font:inherit;font-size:13px}
  textarea{resize:vertical}
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
  .statuspanel{background:#0d1116;border:1px solid var(--line);border-radius:9px;padding:12px;margin-bottom:12px}
  .statusline{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
  .statusline strong{font-size:14px}
  .statgrid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:8px;margin-top:10px}
  .stat{background:#131920;border:1px solid var(--line);border-radius:7px;padding:7px 9px;min-width:0}
  .stat .k{color:var(--dim);font-size:11px;letter-spacing:.3px}
  .stat .v{font-size:12.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .stat .v.err{color:var(--err);white-space:normal;word-break:break-word}
  tr.clickable{cursor:pointer}
  tr.clickable:hover td{background:#1a212a}
  .meta{display:flex;flex-wrap:wrap;gap:6px 18px;font-size:12px;color:var(--dim);margin:2px 0 12px}
  .meta b{color:var(--text);font-weight:500}
  .conv{background:#0d1116;border:1px solid var(--line);border-radius:8px;margin:8px 0;overflow:hidden}
  .conv-head{display:flex;align-items:center;gap:8px;padding:6px 10px;background:#141a21;border-bottom:1px solid var(--line);font-size:12px;color:var(--dim)}
  details.conv>summary{cursor:pointer;list-style:none}
  details.conv>summary::-webkit-details-marker{display:none}
  details.conv>summary::before{content:'▸';color:var(--dim);font-size:11px}
  details.conv[open]>summary::before{content:'▾'}
  .conv-body{padding:10px;white-space:pre-wrap;word-break:break-word;font-family:Consolas,"Microsoft YaHei",monospace;font-size:12.5px;line-height:1.65;max-height:440px;overflow:auto}
  .role{font-weight:600;font-size:11px;letter-spacing:.6px;text-transform:uppercase}
  .role.system{color:#c9a4ff}.role.user{color:#7fb2ff}.role.assistant{color:#3ecf8e}
  .tag.ok{background:#14352a;color:var(--ok)}
  .tag.warn{background:#3a2f18;color:var(--warn)}
  .tag.err{background:#3a1d1d;color:var(--err)}
  .modal{position:fixed;inset:0;background:rgba(4,7,11,.74);display:none;align-items:flex-start;justify-content:center;padding:32px 16px;z-index:50;overflow:auto}
  .modal.open{display:flex}
  .modal-box{background:var(--bg);border:1px solid var(--line);border-radius:12px;width:min(960px,100%);padding:16px 18px 22px;box-shadow:0 24px 70px rgba(0,0,0,.55)}
  .modal-head{display:flex;align-items:center;gap:10px;margin-bottom:10px}
  .modal-head h2{font-size:15px;margin:0;font-weight:600}
  .modal-head button{padding:5px 12px}
  .preview-title{font-size:13px;color:var(--accent);margin:16px 0 4px;font-weight:600}
  .sticker-thumb{width:54px;height:54px;object-fit:cover;border-radius:6px;border:1px solid var(--line);background:#0d1116;display:block}
  .sticker-preview{max-width:180px;max-height:180px;border-radius:8px;border:1px solid var(--line);background:#0d1116;display:none;margin-top:6px}
  .sticker-msg{max-width:150px;max-height:150px;border-radius:8px;border:1px solid var(--line);vertical-align:middle;margin-left:4px}
  .sticker-actions button{padding:2px 8px;font-size:12px;margin:0 2px 2px 0}
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
  <button data-tab="sticker">表情包</button>
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
      <div class="statuspanel">
        <div class="statusline">
          <span class="dot" id="qqDot"></span>
          <strong id="qqStateText">读取中…</strong>
          <span class="tag" id="qqModeTag"></span>
        </div>
        <div class="hint" id="qqSummary" style="margin:5px 0 0">正在读取连接状态…</div>
        <div class="statgrid" id="qqStats"></div>
      </div>
      <label>适配器</label>
      <select id="adapter"><option value="mock">Mock（本地模拟，无需凭据）</option><option value="qq">QQ 官方机器人</option></select>
      <div class="row">
        <div><label>AppID</label><input id="qqAppId"></div>
        <div><label>AppSecret</label><input id="qqSecret" type="password" placeholder="留空表示不修改"></div>
      </div>
      <div class="hint" id="qqSecretHint"></div>
      <div class="hint">接收模式由程序自动识别：收到 <code>GROUP_MESSAGE_CREATE</code> = 群主已开启「接收所有消息」（全量）；
      收到 <code>GROUP_AT_MESSAGE_CREATE</code> = 仅 @。识别结果会显示在「本地试聊 → 当前会话」的「接收」列。</div>
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
      <h2>提示词框架（结构示例，不读取真实会话）</h2>
      <div class="hint" style="margin-top:0">这里只展示 prompt 的结构、当前规则/人设/字数上限；&lt;名字&gt;、&lt;时间&gt; 等是占位符。真实记忆和对话请看「本地试聊 → 当前会话」。</div>
      <div id="previewBody" style="margin-top:10px"><div class="hint">读取中…</div></div>
    </div>
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
      <div class="hint" style="margin:-4px 0 10px">点任意一行可以查看这个会话真正发给 API 的 messages（前缀区 + 历史 + 动态尾部）；会话结束/空闲后仍可查看上一次。</div>
      <table><thead><tr><th>会话</th><th>状态</th><th>接收</th><th>轮次</th><th>待回</th><th>最近活动</th><th>前缀哈希</th><th></th></tr></thead><tbody id="sessions"></tbody></table>
    </div>
  </section>

  <section id="tab-sticker">
    <div class="card">
      <h2>全局设置（关键词与阈值在每张图片上单独设置）</h2>
      <div class="row">
        <div><label>总开关</label><input type="checkbox" id="stickerEnabled"></div>
        <div><label>触发概率（全局）</label><input id="stickerProbability" type="number" min="0" max="1" step="0.01"></div>
        <div><label>触发最低权重（全局）</label><input id="stickerMinWeight" type="number" min="0.1" max="100" step="0.1"></div>
        <div><label>同一聊天冷却（秒）</label><input id="stickerCooldown" type="number" min="0" max="3600" step="1"></div>
        <div><label>没有 msg_id 时按主动消息发送</label><input type="checkbox" id="stickerActive"></div>
      </div>
      <div style="display:flex;gap:8px;align-items:center;margin-top:12px">
        <button class="primary" onclick="saveStickerSettings()">保存全局设置</button>
        <span class="hint" id="stickerSettingsHint" style="margin:0"></span>
      </div>
      <div class="hint" style="margin-top:8px">
        <b>@ 模式</b>：每条入站消息都带 <code>msg_id</code>，表情包作为被动回复紧跟正文发出，不占主动消息频控。<br>
        <b>全量模式</b>：旁听到的普通消息同样带 <code>msg_id</code>，仍走被动回复；只有主动接话（<code>active_tick_sec</code>）
        或这批消息已超过 5 分钟时没有 <code>msg_id</code>，只能按主动消息发送——就是上面那个开关。
      </div>
    </div>

    <div class="card">
      <h2 id="stickerFormTitle">上传表情包</h2>
      <div class="row">
        <div style="flex:2">
          <label>图片（png / jpg / gif / webp，≤ 4MB）</label>
          <input id="stickerFile" type="file" accept="image/png,image/jpeg,image/gif,image/webp">
          <div class="hint">编辑时留空 = 保留原图。</div>
          <label>关键词（逗号 / 分号 / 换行分隔）</label>
          <textarea id="stickerKeywords" rows="3" placeholder="笑死, 哈哈哈:1.5, 无语"></textarea>
          <div class="hint">写法：<code>笑死</code> = 每次命中 1 分；<code>笑死:2</code> = 每次命中 2 分。同一段回复里出现 N 次就算 N 份。阈值是全局的（上方设置），所有图片共用。</div>
          <div class="row">
            <div><label>启用</label><input type="checkbox" id="stickerItemEnabled" checked></div>
          </div>
        </div>
        <div style="flex:1;min-width:200px">
          <label>预览</label>
          <img id="stickerPreview" class="sticker-preview" alt="表情包预览">
        </div>
      </div>
      <div style="display:flex;gap:8px;margin-top:14px">
        <button class="primary" onclick="saveSticker()" id="stickerSaveBtn">上传并保存</button>
        <button class="ghost" onclick="resetStickerForm()">清空 / 取消编辑</button>
      </div>
      <div class="hint" id="stickerSaveHint"></div>
    </div>

    <div class="card">
      <h2>图片库</h2>
      <table>
        <thead><tr><th>预览</th><th>关键词</th><th>状态</th><th>使用次数</th><th>最近发送</th><th>操作</th></tr></thead>
        <tbody id="stickers"></tbody>
      </table>
      <div class="hint">匹配顺序：得分（命中次数 × 权重之和）达到全局阈值 → 取得分最高的一张（同分取 id 小的）→ 按全局概率决定发不发。使用次数只统计真正发出去的。</div>
    </div>

    <div class="card">
      <h2>试算（只算不发）</h2>
      <div style="display:flex;gap:8px">
        <input id="stickerTestText" placeholder="粘一段机器人会说的回复，例如：笑死，好啊那就这么定了">
        <button class="ghost" onclick="testSticker()">试算</button>
      </div>
      <div id="stickerTestResult" style="margin-top:10px"></div>
    </div>

    <div class="card">
      <h2>最近命中</h2>
      <table>
        <thead><tr><th>时间</th><th>聊天</th><th>图片</th><th>得分 / 阈值</th><th>概率 / roll</th><th>方式</th><th>结果</th></tr></thead>
        <tbody id="stickerLog"></tbody>
      </table>
      <div class="hint">「概率不过」= 命中了但骰子没过；「无 msg_id」= 没有可被动回复的消息且主动发送已关闭；「失败」= 上传或发送被 QQ 拒绝，看日志。</div>
    </div>
  </section>
</main>
<div class="modal" id="sessionModal" onclick="if(event.target===this)closeSession()">
  <div class="modal-box">
    <div class="modal-head">
      <span class="dot" id="sessionDot"></span>
      <h2 id="sessionTitle">会话</h2>
      <span style="flex:1"></span>
      <button class="ghost" onclick="refreshSession()">刷新</button>
      <button class="ghost" onclick="closeSession()">关闭</button>
    </div>
    <div id="sessionBody"></div>
  </div>
</div>
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
  if(b.dataset.tab==='tuning'){loadParams();loadPromptFramework();}
  if(b.dataset.tab==='connect')loadStatus();
  if(b.dataset.tab==='sim'){loadStatus();}
  if(b.dataset.tab==='sticker'){loadStickers();loadStickerSettings();loadStickerLog();}
});

const STATE_TEXT = {Idle:'空闲', Active:'激活', Summarizing:'总结中', Closed:'已结束'};

function fmtTime(v){
  if(!v) return '—';
  const d = new Date(v);
  return isNaN(d.getTime()) ? String(v) : d.toLocaleString('zh-CN',{hour12:false});
}
function fmtUnix(sec){
  if(!sec) return '—';
  const d = new Date(sec*1000);
  return isNaN(d.getTime()) ? String(sec) : d.toLocaleString('zh-CN',{hour12:false});
}
function fmtNum(v){return (v==null)?'—':Number(v).toLocaleString('en-US');}

function adapterDot(state){
  if(state==='online') return 'dot on';
  if(state==='mock'||state==='connecting'||state==='pending-restart') return 'dot warn';
  return 'dot err';
}

function renderQqStatus(s){
  const a = s.adapterStatus || {};
  document.getElementById('qqDot').className = adapterDot(a.state);
  const mode = s.adapter==='qq' ? 'QQ 官方机器人' : s.adapter==='mock' ? 'Mock 本地模拟' : (s.adapter||'—');
  document.getElementById('qqModeTag').textContent = mode;
  document.getElementById('qqStateText').textContent =
    a.state==='online' ? '连接正常' :
    a.state==='connecting' ? '连接中…' :
    a.state==='reconnecting' ? '连接已断开，重连中' :
    a.state==='mock' ? '未接入 QQ（本地模拟）' :
    a.state==='pending-restart' ? '等待重启生效' :
    a.state==='error' ? '配置异常' : '未运行';

  const summary = document.getElementById('qqSummary');
  summary.textContent = a.summary || '—';
  summary.style.color = a.online ? 'var(--dim)'
    : (a.state==='mock'||a.state==='connecting'||a.state==='pending-restart')
      ? 'var(--warn)' : 'var(--err)';

  const items = [
    ['当前连接', a.state==='mock' ? '未接入 QQ' : (a.online ? '正常' : '未连接')],
    ['最近收到消息', fmtTime(a.lastInboundAt)],
    ['收 / 发', `${fmtNum(a.inboundCount)} / ${fmtNum(a.outboundCount)}`],
    ['心跳 ack', fmtTime(a.lastHeartbeatAt)],
    ['重连次数', fmtNum(a.reconnects)],
    ['能力', a.receivesAllGroupMessages ? '可收全群消息' : '仅 @ 消息'],
  ];
  if(a.sessionId) items.push(['网关 session', a.sessionId]);
  if(a.gatewayUrl) items.push(['网关地址', a.gatewayUrl]);
  if(a.lastError) items.push(['最近错误', a.lastError, true]);

  let html = items.map(([k,v,isErr])=>
    `<div class="stat" title="${esc(v)}"><div class="k">${esc(k)}</div><div class="v ${isErr?'err':''}">${esc(v)}</div></div>`).join('');
  if(a.detail){
    html += `<div class="stat" style="grid-column:1/-1"><div class="k">说明</div><div class="v" style="white-space:normal;word-break:break-word">${esc(a.detail)}</div></div>`;
  }
  document.getElementById('qqStats').innerHTML = html;
}

function stateTag(st){
  const tone = st==='Active' ? 'ok' : st==='Summarizing' ? 'warn' : '';
  return `<span class="tag ${tone}">${esc(STATE_TEXT[st]||st)}</span>`;
}

function renderSessions(s){
  const tb=document.getElementById('sessions'); tb.innerHTML='';
  const chats=s.chats||[];
  chats.forEach(c=>{
    const name = c.displayName || c.chatId;
    const sub = c.displayName ? `<div><code style="font-size:11px">${esc(c.chatId)}</code></div>` : '';
    const viewId = c.sessionId || c.lastSessionId;
    const canOpen = !!viewId;
    const hear = c.receivesAll===true ? '<span class="tag ok">全量</span>'
      : c.receivesAll===false ? '<span class="tag">仅@</span>' : '<span class="hint">未知</span>';
    const action = canOpen
      ? `<span class="tag" style="color:var(--accent)">${c.sessionId ? '查看 API 对话' : '查看上次对话'}</span>`
      : '<span class="hint">未开会话</span>';
    const click = canOpen ? `onclick="openSession('${esc(viewId)}')"` : '';
    tb.innerHTML += `<tr class="${canOpen?'clickable':''}" ${click}>
      <td>${esc(name)}${sub}</td>
      <td>${stateTag(c.state)}</td>
      <td>${hear}</td>
      <td>${c.turnCount}</td>
      <td>${c.pendingCount}</td>
      <td>${fmtTime(c.lastActivityAt)}</td>
      <td><code>${short(c.prefixHash)}</code></td>
      <td>${action}</td></tr>`;
  });
  if(!chats.length) tb.innerHTML='<tr><td colspan="8" style="color:var(--dim)">还没有会话。发一句话触发一次回复，这里就会出现"进行中的会话"。</td></tr>';
}

async function loadStatus(){
  try{
    const s = await api('/api/status');
    const a = s.adapterStatus || {};
    document.getElementById('dot').className = adapterDot(a.state);
    let text = `${s.adapter==='qq'?'QQ 官方':s.adapter==='mock'?'Mock 模拟':s.adapter} · ${s.persons} 人 / ${s.events} 事件 · ${s.stickers||0} 张表情包 · 今日 ${s.todayTokens} token`;
    if(s.adapter==='qq' && !a.online) text += ' · QQ 未连接';
    if(s.warnings && s.warnings.length) text += ' · ' + s.warnings.length + ' 条配置告警';
    document.getElementById('statusText').textContent = text;
    renderQqStatus(s);
    renderSessions(s);
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
  document.getElementById('qqSecretHint').textContent =
    `AppID：${cfg.qq.appId?'已填':'未填'} · AppSecret：${cfg.qq.secretSet?'已设置（留空不修改）':'未设置'}`;
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
    await loadStatus();
    await loadPromptFramework();
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
      else if(p.type==='int') html+=`<input type="number" step="1" min="${p.min??''}" max="${p.max??''}" data-param="${p.name}" value="${p.value}">`;
      else if(p.type==='double') html+=`<input type="number" step="any" min="${p.min??''}" max="${p.max??''}" data-param="${p.name}" value="${p.value}">`;
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
    await loadPromptFramework();
  }catch(e){toast('保存失败：'+e.message);}
}

// ---------------------------------------------------------------- 提示词框架（调参用）
async function loadPromptFramework(){
  const body=document.getElementById('previewBody');
  if(!body) return;
  body.innerHTML='<div class="hint">生成中…</div>';
  try{
    const f=await api('/api/prompt-framework');
    renderPromptFramework(f);
  }catch(e){
    body.innerHTML='<div class="hint" style="color:var(--err)">框架读取失败：'+esc(e.message)+'</div>';
  }
}

function renderPreviewMessages(messages){
  return (messages||[]).map(m=>`<div class="conv">
    <div class="conv-head">
      <span class="role ${esc(m.role)}">${esc(m.role)}</span>
      <span style="flex:1"></span>
      <span>≈${fmtNum(m.tokens)} token</span>
    </div>
    <div class="conv-body" style="max-height:320px">${esc(m.content)}</div>
  </div>`).join('');
}

function renderPromptFramework(f){
  const cold=f.coldStart||[];
  const sum=f.summary||[];

  let html=`<div class="meta">
    <span><b>模型</b> ${esc(f.model)}</span>
    <span><b>回复上限</b> ${f.replyLength} 字</span>
    <span><b>结构</b> system → user → 总结请求</span>
  </div>`;

  html+=`<h3 class="preview-title">① 冷启动第一轮（结构）</h3>`;
  html+=renderPreviewMessages(cold);
  html+=`<div class="hint">这是骨架：&lt;名字&gt;、&lt;时间&gt;、&lt;本轮消息&gt; 等运行时会替换成真实内容；
    前缀区是缓存命中区，动态尾部每次请求重算。</div>`;

  html+=`<h3 class="preview-title">② 会话结束时的总结请求（结构）</h3>`;
  html+=renderPreviewMessages(sum);
  html+=`<div class="hint">总结触发条件：空闲超时 / 轮次上限 / 历史 token 上限 / 破坏缓存的参数变更 / 手动重建；
    进程重启后未结束且有回合的会话会补一次，空会话不总结。</div>`;

  document.getElementById('previewBody').innerHTML=html;
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

// ---------------------------------------------------------------- 会话详情（API 侧 messages）
let openSessionId = null;

const ROLE_TEXT = {system:'system · 前缀区', user:'user', assistant:'assistant'};
const TURN_KIND_TEXT = {history:'历史', dynamic_tail:'动态尾部', ambient:'旁听群聊', summary_prompt:'总结指令'};
const LIVE_TEXT = {idle:'空闲', active:'激活', summarizing:'总结中', closed:'已结束'};
const END_TEXT = {timeout:'空闲超时', cap:'轮次/长度到顶', drift:'参数漂移', error:'错误', manual:'人工触发', none:''};

async function openSession(sessionId){
  if(!sessionId){ toast('这个会话还没有真正开始'); return; }
  openSessionId = sessionId;
  document.getElementById('sessionModal').classList.add('open');
  document.getElementById('sessionTitle').textContent = '会话 ' + short(sessionId);
  document.getElementById('sessionBody').innerHTML = '<div class="hint">读取中…</div>';
  await refreshSession();
}

async function refreshSession(){
  if(!openSessionId) return;
  try{
    const v = await api('/api/session?sessionId='+encodeURIComponent(openSessionId));
    renderSession(v);
  }catch(e){
    document.getElementById('sessionBody').innerHTML =
      '<div class="hint" style="color:var(--err)">读取失败：'+esc(e.message)+'</div>';
  }
}

function closeSession(){
  document.getElementById('sessionModal').classList.remove('open');
  openSessionId = null;
}

function renderSession(v){
  const stateText = v.liveState ? (LIVE_TEXT[v.liveState]||v.liveState)
    : (v.state==='active' ? '未结束' : (v.state==='closed' ? '已结束' : v.state));
  const endText = (v.state==='closed' && v.endReason && v.endReason!=='none')
    ? '（'+(END_TEXT[v.endReason]||v.endReason)+'）' : '';

  document.getElementById('sessionTitle').textContent =
    '会话 ' + short(v.sessionId) + ' · ' + (v.chatName || v.chatId);
  document.getElementById('sessionDot').className =
    'dot ' + ((v.liveState==='active'||v.state==='active') ? 'on' : '');

  let html = `<div class="hint" style="margin-bottom:10px">
    这是发给 API 的 <code>messages</code> 结构：<code>system</code> 是会话内逐字节冻结的前缀区，
    <code>user</code> 是本轮动态尾部，<code>assistant</code> 是机器人回复。模型看不到没有出现在这里的群聊。
  </div>
  <div class="meta">
    <span><b>会话</b> <code>${esc(v.sessionId)}</code></span>
    <span><b>聊天</b> ${esc(v.chatName||v.chatId)} <code style="font-size:11px">${esc(v.chatId)}</code></span>
    <span><b>状态</b> ${esc(stateText)}${esc(endText)}</span>
    <span><b>轮次</b> ${v.turnCount}</span>
    <span><b>开始</b> ${fmtUnix(v.startedAt)}</span>
    <span><b>结束</b> ${v.endedAt ? fmtUnix(v.endedAt) : '—'}</span>
  </div>
  <div class="meta">
    <span><b>前缀</b> ≈${fmtNum(v.prefixTokens)} token</span>
    <span><b>前缀哈希</b> <code>${esc(v.prefixHash||'—')}</code></span>
    <span><b>累计用量</b> in ${fmtNum(v.tokenIn)} · cached ${fmtNum(v.tokenCached)} · out ${fmtNum(v.tokenOut)}</span>
  </div>`;

  html += `<details class="conv" open>
    <summary class="conv-head"><span class="role system">system</span>
      <span>前缀区（会话内逐字节冻结，命中缓存的部分）</span>
      <span class="tag">≈${fmtNum(v.prefixTokens)} token</span></summary>
    <div class="conv-body">${esc(v.prefixText || '（前缀还没有生成）')}</div>
  </details>`;

  (v.turns||[]).forEach(t=>{
    const kindLabel = t.role==='assistant' ? '回复' : (TURN_KIND_TEXT[t.kind]||t.kind);
    html += `<div class="conv">
      <div class="conv-head">
        <span class="role ${esc(t.role)}">${esc(t.role)}</span>
        <span class="tag">${esc(kindLabel)}</span>
        <span class="tag">#${t.seq}</span>
        <span style="flex:1"></span>
        <span>≈${fmtNum(t.tokens)} token</span>
      </div>
      <div class="conv-body">${esc(t.content)}</div>
    </div>`;
  });
  if(!(v.turns||[]).length){
    html += '<div class="hint">这个会话还没有成功发出过请求，所以只有前缀，没有对话历史。</div>';
  }

  if((v.requests||[]).length){
    html += `<h3 style="font-size:13px;color:var(--accent);margin:18px 0 6px">最近调用（request_log）</h3>
      <table><thead><tr><th>时间</th><th>类型</th><th>模型</th><th>prompt</th><th>命中 / 未命中</th><th>输出</th><th>延迟</th><th>状态</th></tr></thead><tbody>`;
    (v.requests||[]).forEach(r=>{
      const status = r.errorCode
        ? `<span class="tag err">${esc(r.errorCode)}</span>`
        : (r.kind==='summary'
            ? (r.parseOk ? '<span class="tag ok">总结已解析</span>' : '<span class="tag warn">总结未解析</span>')
            : (r.sendConfirmed ? '<span class="tag ok">已确认</span>' : '<span class="tag warn">未确认</span>'));
      html += `<tr><td>${fmtUnix(r.sentAt)}</td><td>${esc(r.kind)}</td><td>${esc(r.model)}</td>
        <td>${fmtNum(r.promptTokens)}</td><td>${fmtNum(r.cacheHitTokens)} / ${fmtNum(r.cacheMissTokens)}</td>
        <td>${fmtNum(r.completionTokens)}</td><td>${r.latencyMs==null?'—':r.latencyMs+'ms'}</td>
        <td>${status}</td></tr>`;
    });
    html += '</tbody></table>';
  }

  document.getElementById('sessionBody').innerHTML = html;
}

document.addEventListener('keydown',e=>{if(e.key==='Escape')closeSession();});

async function simulate(){
  const input=document.getElementById('simInput');
  const text=input.value.trim(); if(!text) return;
  input.value='';
  const log=document.getElementById('chatlog');
  log.innerHTML+=`<div class="msg me"><span class="who">我</span>${esc(text)}</div>`;
  try{
    const r=await api('/api/simulate',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({content:text})});
    (r.replies||[]).forEach(x=>{
      if(x.kind==='sticker') log.innerHTML+=`<div class="msg"><span class="who">${esc(x.label)}</span><img class="sticker-msg" src="${esc(x.imageUrl)}" alt="表情包"></div>`;
      else log.innerHTML+=`<div class="msg"><span class="who">${esc(x.label)}</span>${esc(x.content)}</div>`;
    });
    if(!(r.replies||[]).length) log.innerHTML+=`<div class="msg"><span class="who">系统</span><span style="color:var(--dim)">未触发回复：${esc(r.reason||'')}</span></div>`;
    log.scrollTop=log.scrollHeight;
  }catch(e){log.innerHTML+=`<div class="msg"><span class="who">系统</span>${esc(e.message)}</div>`;}
  loadStatus();
}

document.getElementById('simInput').addEventListener('keydown',e=>{if(e.key==='Enter')simulate();});
// ---------------------------------------------------------------- 表情包
let stickerRows=[], editingStickerId=0, stickerDataUrl='';

const STICKER_RESULT={sent:'已发送',skipped_probability:'概率不过',skipped_active:'无 msg_id',failed:'失败'};
const STICKER_MODE={passive:'被动',active:'主动'};

async function loadStickerSettings(){
  try{
    const ps=await api('/api/params');
    const get=n=>{const p=(ps||[]).find(x=>x.name===n);return p?p.value:undefined;};
    document.getElementById('stickerEnabled').checked=!!get('sticker_enabled');
    document.getElementById('stickerProbability').value=get('sticker_probability')??0.3;
    document.getElementById('stickerMinWeight').value=get('sticker_min_weight')??2;
    document.getElementById('stickerCooldown').value=get('sticker_cooldown_sec')??45;
    document.getElementById('stickerActive').checked=!!get('sticker_active_when_no_msg');
  }catch(e){/* 读不到就保持原样 */}
}

async function saveStickerSettings(){
  const hint=document.getElementById('stickerSettingsHint');
  hint.textContent='保存中…';
  try{
    await api('/api/params',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
      sticker_enabled:document.getElementById('stickerEnabled').checked,
      sticker_probability:Number(document.getElementById('stickerProbability').value),
      sticker_min_weight:Number(document.getElementById('stickerMinWeight').value),
      sticker_cooldown_sec:Number(document.getElementById('stickerCooldown').value),
      sticker_active_when_no_msg:document.getElementById('stickerActive').checked
    })});
    hint.textContent='已保存';
    toast('表情包设置已保存');
    await loadStatus();
  }catch(e){hint.textContent='保存失败：'+e.message;}
}

function showStickerPreview(url){
  const img=document.getElementById('stickerPreview');
  if(url){img.src=url;img.style.display='block';}
  else{img.removeAttribute('src');img.style.display='none';}
}

document.getElementById('stickerFile').addEventListener('change',e=>{
  const f=e.target.files[0];
  if(!f) return;
  if(f.size>4*1024*1024){toast('图片超过 4MB');e.target.value='';return;}
  const reader=new FileReader();
  reader.onload=()=>{stickerDataUrl=reader.result;showStickerPreview(stickerDataUrl);};
  reader.readAsDataURL(f);
});

async function loadStickers(){
  try{
    stickerRows=await api('/api/stickers')||[];
  }catch(e){toast('表情包读取失败：'+e.message);return;}
  const tb=document.getElementById('stickers'); tb.innerHTML='';
  stickerRows.forEach(s=>{
    tb.innerHTML+=`<tr>
      <td>${s.imageUrl?`<img class="sticker-thumb" src="${esc(s.imageUrl)}" alt="">`:'<span class="hint">无图</span>'}</td>
      <td><code>${esc(s.keywords)}</code></td>
      <td>${s.enabled?'<span class="tag ok">启用</span>':'<span class="tag">停用</span>'}</td>
      <td>${s.useCount||0} 次</td>
      <td>${fmtUnix(s.lastSentAt)}</td>
      <td class="sticker-actions">
        <button class="ghost" onclick="editSticker(${s.id})">编辑</button>
        <button class="ghost" onclick="toggleSticker(${s.id},${!s.enabled})">${s.enabled?'停用':'启用'}</button>
        <button class="ghost" style="color:var(--err)" onclick="deleteSticker(${s.id})">删除</button>
      </td></tr>`;
  });
  if(!stickerRows.length) tb.innerHTML='<tr><td colspan="6" style="color:var(--dim)">还没有表情包。上传一张，填几个关键词试试。</td></tr>';
}

function editSticker(id){
  const s=stickerRows.find(x=>x.id===id); if(!s) return;
  editingStickerId=id; stickerDataUrl='';
  document.getElementById('stickerKeywords').value=s.keywords||'';
  document.getElementById('stickerItemEnabled').checked=!!s.enabled;
  document.getElementById('stickerFile').value='';
  showStickerPreview(s.imageUrl);
  document.getElementById('stickerFormTitle').textContent='编辑表情包 #'+id;
  document.getElementById('stickerSaveBtn').textContent='保存修改';
  document.getElementById('stickerSaveHint').textContent=
    `这张图已使用 ${s.useCount||0} 次${s.lastSentAt?'，最近一次 '+fmtUnix(s.lastSentAt):''}`;
  document.getElementById('stickerKeywords').scrollIntoView({behavior:'smooth',block:'center'});
}

function resetStickerForm(){
  editingStickerId=0; stickerDataUrl='';
  document.getElementById('stickerKeywords').value='';
  document.getElementById('stickerItemEnabled').checked=true;
  document.getElementById('stickerFile').value='';
  showStickerPreview('');
  document.getElementById('stickerFormTitle').textContent='上传表情包';
  document.getElementById('stickerSaveBtn').textContent='上传并保存';
  document.getElementById('stickerSaveHint').textContent='';
}

async function saveSticker(){
  const hint=document.getElementById('stickerSaveHint');
  const keywords=document.getElementById('stickerKeywords').value.trim();
  if(!keywords){hint.textContent='至少填一个关键词';return;}
  if(!editingStickerId && !stickerDataUrl){hint.textContent='先选一张图片';return;}
  const fileInput=document.getElementById('stickerFile');
  hint.textContent='保存中…';
  try{
    await api('/api/stickers',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
      id:editingStickerId,
      keywords,
      enabled:document.getElementById('stickerItemEnabled').checked,
      dataUrl:stickerDataUrl,
      fileName:(fileInput.files[0]||{}).name||''
    })});
    toast(editingStickerId?'表情包已更新':'表情包已添加');
    resetStickerForm();
    await loadStickers();
    await loadStatus();
  }catch(e){hint.textContent='保存失败：'+e.message;}
}

async function toggleSticker(id,enabled){
  try{
    await api('/api/stickers/'+id+'/enabled',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled})});
    await loadStickers();
  }catch(e){toast('操作失败：'+e.message);}
}

async function deleteSticker(id){
  if(!confirm('删除这张表情包？图片文件也会一起删掉。')) return;
  try{
    await api('/api/stickers/'+id,{method:'DELETE'});
    toast('已删除');
    if(editingStickerId===id) resetStickerForm();
    await loadStickers();
    await loadStatus();
  }catch(e){toast('删除失败：'+e.message);}
}

async function testSticker(){
  const text=document.getElementById('stickerTestText').value;
  const box=document.getElementById('stickerTestResult');
  if(!text.trim()){box.innerHTML='<div class="hint">先输入一段文本</div>';return;}
  box.innerHTML='<div class="hint">计算中…</div>';
  try{
    const r=await api('/api/stickers/test',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({text})});
    let html=`<div class="hint" style="margin-bottom:8px">总开关 ${r.enabled?'开':'关'} · 全局概率 ${r.probability} · 全局最低权重 ${r.threshold} · ${r.bestId?`本次会选 #${r.bestId}（得分 ${r.bestScore}）`:'没有图片达到阈值'}</div>`;
    html+='<table><thead><tr><th>图片</th><th>关键词</th><th>得分 / 阈值</th><th>命中</th><th>过阈值</th></tr></thead><tbody>';
    (r.scores||[]).forEach(s=>{
      html+=`<tr><td><img class="sticker-thumb" src="${esc(s.imageUrl)}" alt=""></td>
        <td><code>${esc(s.keywords)}</code>${s.enabled?'':' <span class="tag">停用</span>'}</td>
        <td>${s.score} / ${s.threshold}</td>
        <td>${esc(s.hitText)||'—'}</td>
        <td>${s.meets?'<span class="tag ok">是</span>':'<span class="tag">否</span>'}</td></tr>`;
    });
    html+='</tbody></table>';
    box.innerHTML=html;
  }catch(e){box.innerHTML='<div class="hint" style="color:var(--err)">试算失败：'+esc(e.message)+'</div>';}
}

async function loadStickerLog(){
  try{
    const rows=await api('/api/stickers/log?limit=50')||[];
    const tb=document.getElementById('stickerLog'); tb.innerHTML='';
    rows.forEach(r=>{
      const tone=r.result==='sent'?'ok':r.result==='failed'?'err':'';
      tb.innerHTML+=`<tr><td>${fmtUnix(r.createdAt)}</td>
        <td><code style="font-size:11px">${esc(short(r.chatId))}</code></td>
        <td><code>${esc(r.keywords)}</code><div class="hint">#${r.stickerId}</div></td>
        <td>${r.score} / ${r.threshold}</td>
        <td>${r.probability} / ${r.roll}</td>
        <td>${esc(STICKER_MODE[r.mode]||r.mode)}</td>
        <td><span class="tag ${tone}">${esc(STICKER_RESULT[r.result]||r.result)}</span></td></tr>`;
    });
    if(!rows.length) tb.innerHTML='<tr><td colspan="7" style="color:var(--dim)">还没有命中记录。回复里出现关键词后这里会有记录（被概率拦下的也会记）。</td></tr>';
  }catch(e){/* 忽略 */}
}

function esc(s){return (s==null?'':String(s)).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
function short(h){return h?h.slice(0,14)+'…':'—';}
loadConfig();loadStatus();setInterval(loadStatus,5000);
</script>
</body>
</html>
""";
}
