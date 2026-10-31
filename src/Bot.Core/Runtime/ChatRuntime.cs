using System.Collections.Concurrent;
using System.Text;
using Bot.Core.Abstractions;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Llm;
using Bot.Core.Memory;
using Bot.Core.Text;
using Microsoft.Extensions.Logging;

namespace Bot.Core.Runtime;

/// <summary>
/// 运行时编排：入站 → 触发决策 → 三区组装 → 调用 → 投递 → 轮换。
/// <para>线程模型：每个 chat 一个 Gate 串行执行（Actor 模式），跨群并行；
/// 全局只有一个 250ms 的调度循环，未命中触发时不做任何 API 调用。</para>
/// </summary>
public sealed class ChatRuntime : IAsyncDisposable
{
    private readonly Db _db;
    private readonly IClock _clock;
    private readonly ConfigService _config;
    private readonly IdentityService _identity;
    private readonly EventStore _events;
    private readonly RetrievalService _retrieval;
    private readonly PromptAssembler _assembler;
    private readonly SessionManager _sessions;
    private readonly RequestLogStore _requestLog;
    private readonly WalStore _wal;
    private readonly SummaryApplier _summaryApplier;
    private readonly TokenEstimator _estimator;
    private readonly IChatAdapter _adapter;
    private readonly ModelProvider _models;
    private readonly ILogger<ChatRuntime> _log;
    private readonly TriggerEngine _trigger;
    private readonly StickerStore? _stickers;

    private readonly ConcurrentDictionary<string, ChatState> _chats = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private Task? _recovery;

    public ChatRuntime(
        Db db,
        IClock clock,
        ConfigService config,
        IdentityService identity,
        EventStore events,
        RetrievalService retrieval,
        PromptAssembler assembler,
        SessionManager sessions,
        RequestLogStore requestLog,
        WalStore wal,
        SummaryApplier summaryApplier,
        TokenEstimator estimator,
        IChatAdapter adapter,
        ModelProvider models,
        ILogger<ChatRuntime>? log = null,
        TriggerEngine? trigger = null,
        StickerStore? stickers = null)
    {
        _db = db;
        _clock = clock;
        _config = config;
        _identity = identity;
        _events = events;
        _retrieval = retrieval;
        _assembler = assembler;
        _sessions = sessions;
        _requestLog = requestLog;
        _wal = wal;
        _summaryApplier = summaryApplier;
        _estimator = estimator;
        _adapter = adapter;
        _models = models;
        _trigger = trigger ?? new TriggerEngine();
        _stickers = stickers;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatRuntime>.Instance;
    }

    public int TickIntervalMs { get; set; } = 250;

    /// <summary>表情包概率掷骰子。默认随机；测试可替换成固定值。</summary>
    public Func<double> StickerRoll { get; set; } = static () => Random.Shared.NextDouble();

    /// <summary>未读缓冲的硬上限，防内存失控；真正的过期由 backfill_window_min 控制。</summary>
    private const int UnseenCapacity = 200;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _loop = LoopAsync(_cts.Token);

        // 恢复必须放后台：进程重启时要重放 WAL、给孤儿会话补总结，这些都要调模型。
        // 如果 API 不可达（断网/超时），同步等待会让托盘和配置页永远起不来。
        _recovery = Task.Run(() => SafeRecoverAsync(cancellationToken), CancellationToken.None);

        await Task.Yield();
    }

    private async Task SafeRecoverAsync(CancellationToken token)
    {
        try
        {
            await RecoverAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "启动恢复失败");
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        if (_recovery is not null)
        {
            // 退出时不等待恢复完成（可能正在等一个 30s 的模型超时）。
            await Task.WhenAny(_recovery, Task.Delay(2000)).ConfigureAwait(false);
        }
    }

    /// <summary>入站入口。适配器必须尽快返回，重活丢给调度循环。</summary>
    public Task EnqueueAsync(IncomingMessage message)
    {
        // 指令消息不进对话、不触发模型。
        if (!message.IsSelf && NameCommand.TryParse(message.Content, out var requestedName))
        {
            return HandleNameCommandAsync(message, requestedName);
        }

        // 纯表情 / 纯占位符（[表情]）不进入任何缓冲、历史或数据库。
        if (TriggerEngine.IsOnlyNoise(message.Content))
        {
            _log.LogInformation("[{Chat}] 收到纯表情/占位内容，已忽略", Mask(message.ChatId));
            return Task.CompletedTask;
        }

        var state = _chats.GetOrAdd(message.ChatId, id => new ChatState(id));

        // 接收模式只看平台事件：GROUP_MESSAGE_CREATE = 群主开了全量，GROUP_AT_MESSAGE_CREATE = 仅 @。
        // 不再依赖手动开关——手动开了但群没开时，会把"仅 @"的群误当成能旁听全群。
        if (message.Kind == ChatKind.Group)
        {
            if (string.Equals(message.EventType, "GROUP_MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase))
            {
                state.ReceivesAllGroupMessages = true;
            }
            else if (string.Equals(message.EventType, "GROUP_AT_MESSAGE_CREATE", StringComparison.OrdinalIgnoreCase))
            {
                state.ReceivesAllGroupMessages = false;
            }
            else
            {
                state.ReceivesAllGroupMessages ??= _adapter.Capabilities.ReceivesAllGroupMessages ? true : null;
            }
        }

        var botLabel = _config.Raw.Persona.BotName;
        var cfg = _config.ForChat(message.ChatId);

        var person = message.IsSelf ? null : _identity.Resolve(message, botLabel);
        var label = message.IsSelf ? botLabel : person!.DisplayLabel;

        SaveMessage(message, label);

        lock (state.Sync)
        {
            state.Recent.Add(message);
            if (state.Recent.Count > 60)
            {
                state.Recent.RemoveRange(0, state.Recent.Count - 60);
            }

            state.Kind = message.Kind;
            state.DisplayName ??= message.ChatDisplayName;

            if (message.IsSelf)
            {
                return Task.CompletedTask;
            }

            state.Labels[message.OpenId] = label;
            state.Pending.Add(message);

            // 「自上次回复以来群里的发言」：触发时会被补进上下文。
            // 必须带时间窗——否则整晚没被触发时，几小时前的旧话题会被当成"最近群聊"灌进新会话。
            state.Unseen.Add(message);
            var unseenCutoff = _clock.UtcNow.AddMinutes(-cfg.BackfillWindowMin);
            state.Unseen.RemoveAll(m => m.Timestamp < unseenCutoff);
            if (state.Unseen.Count > UnseenCapacity)
            {
                state.Unseen.RemoveRange(0, state.Unseen.Count - UnseenCapacity);
            }

            state.FirstPendingAt ??= message.Timestamp;
            state.LastMessageAt = message.Timestamp;
            state.ConsecutiveUnanswered = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>`名字=XXX`：直接改档案并回一句，不调用模型、不进历史。没有档案时按 open_id 建档。</summary>
    private async Task HandleNameCommandAsync(IncomingMessage message, string requestedName)
    {
        try
        {
            var check = NameRules.Check(requestedName, _config.Raw.Persona.BotName);
            if (!check.Ok)
            {
                await SendCommandReplyAsync(message, $"名字没改成：{check.Reason}");
                return;
            }

            var person = _identity.EnsurePerson(message.OpenId, message.UnionOpenId, string.Empty);
            var result = _identity.TryApplyName(person.PersonId, check.Name, "manual");

            var reply = result.Ok
                ? (result.Changed ? $"名字已改为「{result.Name}」" : $"名字已经是「{result.Name}」")
                : $"名字没改成：{result.Message}";

            await SendCommandReplyAsync(message, reply);
            _log.LogInformation("[{Chat}] 收到名字指令：{Reply}", Mask(message.ChatId), reply);
        }
        catch (OperationCanceledException)
        {
            // 关闭中
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Chat}] 名字指令执行失败", Mask(message.ChatId));
        }
    }

    private async Task SendCommandReplyAsync(IncomingMessage source, string content)
    {
        try
        {
            await _adapter.SendAsync(new OutgoingMessage(
                source.ChatId,
                source.Kind,
                content,
                // 指令回复必须是被动回复（带 msg_id），否则在未开启全量接收的群里会被当成主动消息拒发。
                // 不带 MentionOpenId：QQ 群文本消息不支持嵌入 @，带标签会原样显示。
                ReplyToMessageId: source.MessageId), _cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Chat}] 指令回复发送失败", Mask(source.ChatId));
        }
    }

    /// <summary>调度一次。测试可以直接调用，不必等待定时器。</summary>
    public async Task TickAsync()
    {
        var now = _clock.UtcNow;

        foreach (var state in _chats.Values)
        {
            bool due;
            lock (state.Sync)
            {
                due = IsBatchDue(state, now);
                if (state.CooldownUntil is { } until && now >= until)
                {
                    state.CooldownUntil = null;
                }
            }

            if (due)
            {
                await FireAsync(state).ConfigureAwait(false);
            }
        }

        // 会话激活后的"主动接话"节拍：每隔 active_tick_sec 把这段时间的群聊汇总进来回一次。
        foreach (var state in _chats.Values)
        {
            await CheckCadenceAsync(state, now).ConfigureAwait(false);
        }

        await CheckRotationsAsync().ConfigureAwait(false);
    }

    /// <summary>到点了就主动接一次话（只在全量模式、会话已激活、且有新群聊时）。</summary>
    private async Task CheckCadenceAsync(ChatState state, DateTimeOffset now)
    {
        if (state.Kind != ChatKind.Group)
        {
            return; // 主动接话只针对群聊
        }

        var cfg = _config.ForChat(state.ChatId);
        if (cfg.ActiveTickSec <= 0 || IsPaused(state.ChatId))
        {
            return;
        }

        if (!CanHearAllGroupMessages(state))
        {
            return; // 收不到全群发言时，主动接话没有素材
        }

        var session = state.Session;
        if (session is not { State: SessionState.Active } || !session.HasLiveContext)
        {
            return; // 还没被唤醒过，不该主动开口
        }

        lock (state.Sync)
        {
            // 有消息排队等触发时，先让 FireAsync 处理：
            // 否则节拍可能先把这条 @ 消息汇总回复一次，防抖结束后 @ 再回一次，造成重复回复。
            if (state.Pending.Count > 0)
            {
                return;
            }

            if (state.Unseen.Count == 0)
            {
                return;
            }

            if (state.NextCadenceAt is { } next && now < next)
            {
                return;
            }
        }

        await FireCadenceAsync(state, cfg).ConfigureAwait(false);
    }

    private async Task FireCadenceAsync(ChatState state, EffectiveConfig cfg)
    {
        await state.Gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            if (state.CooldownUntil is { } until && now < until)
            {
                return;
            }

            var session = state.Session;
            if (session is null || session.State != SessionState.Active || !session.HasLiveContext)
            {
                return;
            }

            IncomingMessage[] batch;
            lock (state.Sync)
            {
                // 拿 Gate 期间可能又来了新消息（包括 @）；有 pending 就让触发路径先处理。
                if (state.Pending.Count > 0)
                {
                    return;
                }

                if (state.Unseen.Count == 0)
                {
                    return;
                }

                // 节拍同样只看时间窗内的消息：隔了几小时的旧发言不该被当成"这段时间的闲聊"。
                var cutoff = now.AddMinutes(-cfg.BackfillWindowMin);
                batch = state.Unseen.Where(m => m.Timestamp >= cutoff).ToArray();
                if (batch.Length == 0)
                {
                    state.Unseen.RemoveAll(m => m.Timestamp < cutoff);
                    return;
                }
            }

            _log.LogInformation("[{Chat}] 主动接话（每 {Sec}s 一次）：把这期间的 {Count} 条群聊汇总进来",
                Mask(state.ChatId), cfg.ActiveTickSec, batch.Length);

            await RespondAsync(state, session, batch, Array.Empty<IncomingMessage>(), cfg).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关闭中
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Chat}] 主动接话失败", Mask(state.ChatId));
        }
        finally
        {
            // 无论成败都推迟下一次，避免模型出错时每 250ms 打一次。
            if (cfg.ActiveTickSec > 0)
            {
                lock (state.Sync)
                {
                    state.NextCadenceAt = _clock.UtcNow.AddSeconds(cfg.ActiveTickSec);
                }
            }

            state.Gate.Release();
        }
    }

    public IReadOnlyList<ChatSnapshot> Snapshot() =>
        _chats.Values.Select(state => new ChatSnapshot(
                state.ChatId,
                state.Kind,
                state.DisplayName,
                state.Session?.SessionId,
                state.Session?.State ?? SessionState.Idle,
                state.Session?.TurnCount ?? 0,
                state.Pending.Count,
                state.CooldownUntil,
                state.Session?.PrefixHash,
                state.Session?.LastActivityAt,
                state.ReceivesAllGroupMessages))
            .ToArray();

    /// <summary>强制对某个群立即轮换（配置页"重建会话"按钮）。</summary>
    public Task ForceRotateAsync(string chatId) =>
        _chats.TryGetValue(chatId, out var state)
            ? RotateAsync(state, RotationReason.Manual, "人工触发")
            : Task.CompletedTask;

    // ------------------------------------------------------------------ 调度

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await TickAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "调度循环异常");
            }

            try
            {
                await Task.Delay(TickIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool IsBatchDue(ChatState state, DateTimeOffset now)
    {
        if (state.Pending.Count == 0)
        {
            return false;
        }

        var cfg = _config.ForChat(state.ChatId);
        var first = state.FirstPendingAt ?? now;
        var last = state.LastMessageAt ?? now;
        var density = state.Recent.Count(m => (now - m.Timestamp).TotalSeconds <= 60);
        return Debounce.Remaining(first, last, now, cfg, density) <= TimeSpan.Zero;
    }

    private async Task FireAsync(ChatState state)
    {
        await state.Gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            var cfg = _config.ForChat(state.ChatId);
            IncomingMessage[] batch;
            IncomingMessage[] background;
            lock (state.Sync)
            {
                if (state.Pending.Count == 0)
                {
                    return;
                }

                batch = state.Pending.ToArray();
                state.Pending.Clear();
                state.FirstPendingAt = null;
                state.LastMessageAt = null;

                // 本批之前那些"没人叫它、也没触发"的发言，补进上下文当背景。
                // 只取时间窗内的：过期的一律不补，否则会拿几小时前的旧话题冒充"当前对话"。
                var cap = Math.Max(0, cfg.BackfillMaxMsgs);
                var cutoff = _clock.UtcNow.AddMinutes(-cfg.BackfillWindowMin);
                background = cap == 0
                    ? Array.Empty<IncomingMessage>()
                    : state.Unseen.Where(m => !batch.Contains(m) && m.Timestamp >= cutoff).ToArray();
                if (background.Length > cap)
                {
                    background = background[^cap..];
                }
            }

            if (IsPaused(state.ChatId))
            {
                _log.LogInformation("[{Chat}] 已暂停，丢弃 {Count} 条待回复", Mask(state.ChatId), batch.Length);
                return;
            }

            if (state.Session is { State: SessionState.Summarizing })
            {
                BufferPending(state, batch);
                return;
            }

            var triggerContext = BuildTriggerContext(state, cfg);
            var outcome = _trigger.EvaluateBatch(batch, triggerContext, cfg);
            if (!outcome.Triggered)
            {
                // 会话已经开起来了，那群里说的话就应该进对话——不然它每回一次就"失忆"一次。
                CaptureAmbient(state, batch, triggerContext, cfg);
                _log.LogInformation("[{Chat}] 不回：{Reason}", Mask(state.ChatId), outcome.Reason);
                return;
            }

            _log.LogInformation("[{Chat}] 触发：{Source} / {Decision} / {Reason}｜背景 {Background} 条",
                Mask(state.ChatId), outcome.Source, outcome.Decision, outcome.Reason, background.Length);

            var session = state.Session;
            if (session is null || session.State == SessionState.Closed || !session.HasLiveContext)
            {
                session = OpenSession(state, batch, cfg);
            }

            await RespondAsync(state, session, batch, background, cfg).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关闭中
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Chat}] 处理消息失败", Mask(state.ChatId));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// 会话进行中、这一批又不该回复时，把它并入对话（不调用模型）。
    /// <para>
    /// 这样机器人"回完一句之后一直在听"，下一轮请求能看到中间所有发言，
    /// 而不是像之前那样：没触发的批次被丢掉，下次被叫到时对中间的对话一无所知。
    /// </para>
    /// <para>用 <see cref="TurnKind.Ambient"/> 记账：进历史但<b>不计轮次</b>，避免话多的群几分钟就把会话撑到轮换。</para>
    /// </summary>
    private void CaptureAmbient(ChatState state, IReadOnlyList<IncomingMessage> batch, TriggerContext context,
        EffectiveConfig cfg)
    {
        // 开了"主动接话节拍"时，这批消息要留着等节拍一次性喂给模型，不能现在就并进历史。
        if (cfg.ActiveTickSec > 0)
        {
            return;
        }

        if (!cfg.CaptureAmbient || !context.ReceivesAllMessages)
        {
            return;
        }

        var session = state.Session;
        if (session is not { State: SessionState.Active } || !session.HasLiveContext)
        {
            return;
        }

        // 纯表情/空内容不值得占历史位。
        if (batch.All(m => TriggerEngine.IsOnlyNoise(m.Content)))
        {
            return;
        }

        var tail = BuildTail(state, batch, Array.Empty<IncomingMessage>(),
            Array.Empty<PersonRow>(), Array.Empty<EventRow>(), cfg);
        AppendTurn(state, session, new SessionTurn("user", tail.Text, TurnKind.Ambient));
        session.Touch(_clock.UtcNow);
        _sessions.SaveProgress(session);

        lock (state.Sync)
        {
            foreach (var message in batch)
            {
                state.Unseen.Remove(message);
            }
        }

        _log.LogInformation("[{Chat}] 不回复，但已并入对话（{Count} 条，不计轮次）", Mask(state.ChatId), batch.Count);
    }

    private TriggerContext BuildTriggerContext(ChatState state, EffectiveConfig cfg)
    {
        lock (state.Sync)
        {
            return new TriggerContext(
                _clock.UtcNow,
                state.Recent.ToArray(),
                CanHearAllGroupMessages(state),
                state.ConsecutiveUnanswered,
                state.CooldownUntil,
                _config.Raw.Persona.BotName);
        }
    }

    /// <summary>这个群是否真的能收到全量消息：优先用平台事件推出来的每群状态，未知时才退回适配器能力。</summary>
    private bool CanHearAllGroupMessages(ChatState state) =>
        state.ReceivesAllGroupMessages ?? _adapter.Capabilities.ReceivesAllGroupMessages;

    // ------------------------------------------------------------------ 会话开合

    private Session OpenSession(ChatState state, IReadOnlyList<IncomingMessage> batch, EffectiveConfig cfg)
    {
        var session = _sessions.Start(state.ChatId);

        // 冷启动加载"这一轮真正相关的人"：发言者 + 消息内容里点到名的人。
        // 不再把群成员画像全塞进前缀；没参与当前对话的档案不占 token、不干扰模型。
        var queryText = string.Join('\n', batch.Select(m => m.Content));
        var persons = _identity.ListInvolved(state.ChatId, batch.Select(m => m.OpenId), queryText)
            .Where(HasProfile)
            .ToArray();
        var personIds = persons.Select(p => p.PersonId).ToArray();

        var retrieved = _retrieval.Retrieve(state.ChatId, queryText, personIds, cfg);

        var prefix = _assembler.BuildPrefix(
            state.ChatId,
            cfg,
            _config.Raw.Persona,
            state.Kind,
            persons,
            retrieved.Select(r => r.Event).ToArray(),
            state.LastSummary);

        session.FreezePrefix(prefix.Text, prefix.Hash, prefix.Tokens);
        session.Activate();
        _sessions.SavePrefix(session);
        _events.TouchInjected(retrieved.Select(r => r.Event.EventId).ToArray());

        state.Session = session;
        state.Seq = 0;
        state.Fingerprint = Fingerprint(cfg);
        state.NextCadenceAt = cfg.ActiveTickSec > 0 ? _clock.UtcNow.AddSeconds(cfg.ActiveTickSec) : null;

        // 记下这个前缀里到底放了哪些人和事，后面才知道"谁还没给过"。
        state.PrefixPersonIds = prefix.PersonIds ?? Array.Empty<int>();
        state.PrefixEventIds = prefix.EventIds ?? Array.Empty<long>();
        state.TailPersonIds.Clear();
        state.TailEventIds.Clear();

        // 不要在这里清 Labels：本批消息的标签是入站时算好并冻结的，
        // 清掉会让"首条消息"在动态尾部显示成 群友??。标签只在收到新消息时按需刷新。

        _log.LogInformation("[{Chat}] 开新会话 {Session}｜前缀 {Tokens} token｜注入事件 {Events} 条｜人物 {Persons} 人{Trimmed}｜冷启动",
            Mask(state.ChatId), session.SessionId, prefix.Tokens, retrieved.Count, persons.Length,
            prefix.PersonsTrimmed > 0 ? $"（按预算裁掉 {prefix.PersonsTrimmed} 人）" : string.Empty);

        return session;
    }

    // ------------------------------------------------------------------ 请求

    private async Task RespondAsync(ChatState state, Session session, IReadOnlyList<IncomingMessage> batch,
        IReadOnlyList<IncomingMessage> background, EffectiveConfig cfg)
    {
        var now = _clock.UtcNow;

        // 前缀守护（文档 8.2）：冻结副本一旦被改动，后面每一轮都在为"永不命中"付全价。
        if (cfg.PrefixDriftAlert)
        {
            var actual = PromptHasher.Compute(session.PrefixText);
            if (!string.Equals(actual, session.PrefixHash, StringComparison.Ordinal))
            {
                _log.LogError("E_PREFIX_DRIFT 会话 {Session} 前缀被改写（期望 {Expected}，实际 {Actual}）",
                    session.SessionId, session.PrefixHash, actual);
            }
        }

        var (extraPersons, extraEvents) = BuildSupplement(state, batch, cfg);
        var tail = BuildTail(state, batch, background, extraPersons, extraEvents, cfg);
        if (tail.ScrubbedIds > 0)
        {
            _log.LogError("E_PROMPT_LEAK 动态尾部里出现了 {Count} 个不透明标识（open_id 形态），已替换为 <id>。"
                          + "这说明某处又在拿 chat_id / open_id 当展示名用。", tail.ScrubbedIds);
        }
        var historyTokens = _estimator.EstimateMessages(session.Turns.Select(t => t.Content));
        var messages = new List<PromptMessage>(session.Turns.Count + 2)
        {
            new("system", session.PrefixText!),
        };
        messages.AddRange(session.Turns.Select(t => new PromptMessage(t.Role, t.Content)));
        messages.Add(new PromptMessage("user", tail.Text));

        var seq = ++state.Seq;
        var idemKey = $"{session.SessionId}:{seq}";
        var requestId = Guid.NewGuid().ToString("N")[..16];

        _requestLog.Insert(new RequestLogRow(
            requestId, session.SessionId, state.ChatId, seq, "chat", cfg.Model, TimeUtil.Unix(now),
            session.PrefixHash ?? string.Empty, session.PrefixTokens, historyTokens, tail.Tokens,
            null, null, null, null, null, true, null, idemKey, false, null));

        var model = _models.Get(_config.Raw.Llm);
        var result = await model.CompleteAsync(
            new ChatCompletionRequest(cfg.Model, messages, cfg.Temperature, _config.Raw.Llm.MaxOutputTokens, cfg.Thinking),
            _cts.Token).ConfigureAwait(false);

        if (!result.Ok)
        {
            _log.LogWarning("[{Chat}] 调用失败 {Code}：{Message}", Mask(state.ChatId), result.ErrorCode, result.ErrorMessage);
            _db.Exec("UPDATE request_log SET error_code=@e, latency_ms=@l WHERE request_id=@id",
                ("@e", result.ErrorCode), ("@l", result.LatencyMs), ("@id", requestId));
            return;
        }

        var plan = Delivery.Plan(result.Text!, cfg);
        if (plan.Bubbles.Count == 0)
        {
            _log.LogWarning("[{Chat}] 空回复，静默", Mask(state.ChatId));
            _db.Exec("UPDATE request_log SET error_code=@e WHERE request_id=@id",
                ("@e", ErrorCodes.Empty), ("@id", requestId));
            return;
        }

        // 成功后一次性追加，保证历史区只追加（文档 1.3 铁律 2）。
        AppendTurn(state, session, new SessionTurn("user", tail.Text, TurnKind.DynamicTail));
        AppendTurn(state, session, new SessionTurn("assistant", string.Join('\n', plan.Bubbles)));

        // 这批（含补给它的背景）已经进了历史，从"未读"里摘掉；调用失败的批次则保留，下次继续补。
        lock (state.Sync)
        {
            foreach (var message in background)
            {
                state.Unseen.Remove(message);
            }

            foreach (var message in batch)
            {
                state.Unseen.Remove(message);
            }
        }

        // 补过的记忆记下来，本会话内不再重复补。
        foreach (var person in extraPersons)
        {
            state.TailPersonIds.Add(person.PersonId);
        }

        foreach (var item in extraEvents)
        {
            state.TailEventIds.Add(item.EventId);
        }

        session.RecordUsage(result.Usage.PromptTokens, result.Usage.CacheHitTokens, result.Usage.CompletionTokens);
        session.Touch(now);
        _estimator.Observe(_estimator.EstimateMessages(messages.Select(m => m.Content)), result.Usage.PromptTokens);
        _sessions.SaveProgress(session);

        var mentionOpenId = ResolveMention(state, plan.MentionLabel);

        // QQ 的被动回复要求 msg_id 在 5 分钟内。主动接话的节拍可能比这更长，
        // 超时就别带 msg_id——否则接口会拒发，用户看到的是"机器人哑了"。
        var replyTo = batch[^1].MessageId;
        if (batch[^1].Timestamp != default
            && now - batch[^1].Timestamp > TimeSpan.FromMinutes(4.5))
        {
            replyTo = null;
            _log.LogInformation("[{Chat}] 这批消息已超过 4.5 分钟，改按主动消息发送（不带 msg_id）", Mask(state.ChatId));
        }

        var delivered = await DeliverAsync(state, plan, mentionOpenId, idemKey, replyTo, cfg).ConfigureAwait(false);

        _requestLog.MarkConfirmed(requestId,
            result.Usage.CompletionTokens,
            result.Usage.PromptTokens,
            result.Usage.CacheHitTokens,
            result.Usage.CacheMissTokens,
            result.LatencyMs);

        // 表情包只在正文真的发出去之后补一张；它不追加回合、不写 message 表，
        // 所以模型下一轮完全不知道发过图（需求：表情包不纳入原有对话）。
        // QQ 同一个 msg_id 最多回 5 次：已经用满 5 条气泡时就别再补图，否则会被接口拒掉。
        var hasStickerRoom = replyTo is null || plan.Bubbles.Count < 5;
        if (delivered && hasStickerRoom)
        {
            await TrySendStickerAsync(state, string.Join('\n', plan.Bubbles), replyTo, idemKey, cfg)
                .ConfigureAwait(false);
        }

        // 连续无人接话 → 静默冷却（文档 3.1.2）
        state.ConsecutiveUnanswered++;
        if (state.ConsecutiveUnanswered >= 3)
        {
            var until = now.AddMinutes(cfg.CooldownMin);
            state.CooldownUntil = until;
            PersistChatState(state.ChatId, state.LastSummary, until);
            _log.LogInformation("[{Chat}] 连续 {N} 条无人接话，冷却至 {Until}",
                Mask(state.ChatId), state.ConsecutiveUnanswered, TimeText.Format(until));
        }

        // 正常回复也算一次"开口"，把主动接话的节拍往后推。
        if (cfg.ActiveTickSec > 0)
        {
            lock (state.Sync)
            {
                state.NextCadenceAt = now.AddSeconds(cfg.ActiveTickSec);
            }
        }
    }

    /// <summary>
    /// 会话中途的按需补充：这轮发言者 / 这轮聊到的事，如果前缀里没有，就挑几条补进尾部。
    /// <para>只补一次：补过的记进 <c>TailPersonIds</c> / <c>TailEventIds</c>，后面靠历史自己携带。</para>
    /// </summary>
    private (IReadOnlyList<PersonRow> Persons, IReadOnlyList<EventRow> Events) BuildSupplement(
        ChatState state, IReadOnlyList<IncomingMessage> batch, EffectiveConfig cfg)
    {
        if (!cfg.OnDemandMemory || batch.Count == 0)
        {
            return (Array.Empty<PersonRow>(), Array.Empty<EventRow>());
        }

        var limit = cfg.OnDemandMaxItems;
        var persons = new List<PersonRow>();
        var events = new List<EventRow>();

        var query = string.Join('\n', batch.Select(m => m.Content));
        var involved = _identity.ListInvolved(state.ChatId, batch.Select(m => m.OpenId), query).ToArray();
        var involvedIds = involved.Select(p => p.PersonId).ToArray();

        foreach (var person in involved.Where(HasProfile))
        {
            if (persons.Count + events.Count >= limit)
            {
                break;
            }

            if (state.PrefixPersonIds.Contains(person.PersonId) || state.TailPersonIds.Contains(person.PersonId))
            {
                continue;
            }

            persons.Add(person);
        }

        if (persons.Count + events.Count < limit)
        {
            foreach (var hit in _retrieval.Retrieve(state.ChatId, query, involvedIds, cfg)
                         .OrderByDescending(h => h.Score))
            {
                if (persons.Count + events.Count >= limit)
                {
                    break;
                }

                var eventId = hit.Event.EventId;
                if (state.PrefixEventIds.Contains(eventId) || state.TailEventIds.Contains(eventId))
                {
                    continue;
                }

                if (events.Any(e => e.EventId == eventId))
                {
                    continue;
                }

                events.Add(hit.Event);
            }
        }

        return (persons, events);
    }

    private PromptTail BuildTail(ChatState state, IReadOnlyList<IncomingMessage> batch,
        IReadOnlyList<IncomingMessage> background, IReadOnlyList<PersonRow> extraPersons,
        IReadOnlyList<EventRow> extraEvents, EffectiveConfig cfg)
    {
        var tailMessages = batch.Select(m => new TailMessage(LabelOfLocked(state, m), m.Content)).ToArray();
        var backgroundMessages = background.Select(m => new TailMessage(LabelOfLocked(state, m), m.Content)).ToArray();

        return _assembler.BuildTail(new PromptTailContext(
            state.ChatId,
            state.DisplayName,
            state.Kind,
            _clock.UtcNow,
            tailMessages,
            cfg.ReplyLength,
            backgroundMessages,
            extraPersons,
            extraEvents));
    }

    private async Task<bool> DeliverAsync(ChatState state, ReplyPlan plan, string? mentionOpenId, string idemKey,
        string? replyToMessageId, EffectiveConfig cfg)
    {
        var delay = Delivery.TypingDelay(plan.Bubbles[0], cfg);
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        for (var i = 0; i < plan.Bubbles.Count; i++)
        {
            if (i > 0)
            {
                var gap = Delivery.BubbleGap(cfg);
                if (gap > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(gap, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }

            var message = new OutgoingMessage(
                state.ChatId,
                state.Kind,
                plan.Bubbles[i],
                i == 0 ? mentionOpenId : null,
                replyToMessageId,
                $"{idemKey}:{i}");

            try
            {
                await _adapter.SendAsync(message, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[{Chat}] 气泡发送失败", Mask(state.ChatId));
                _wal.Add(state.Session?.SessionId ?? "-", WalStore.KindSend,
                    System.Text.Json.JsonSerializer.Serialize(message));
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 正文发出后按关键词匹配图片库，可能追加一张表情包。
    /// <para>
    /// 顺序：算分（命中次数 × 关键词权重）→ 未启用 / 未过阈值淘汰 → 取得分最高（同分取 id 小的）
    /// → 全局概率掷骰子 → 发送。发送失败只记日志，不影响已经发出的回复。
    /// </para>
    /// <para>
    /// 被动 / 主动：带 msg_id 是 QQ 的被动回复（@ 模式、全量模式下 5 分钟内的普通回复都走这条）；
    /// 主动接话或消息超过 4.5 分钟时没有 msg_id，只能按主动消息发，可用参数关掉。
    /// </para>
    /// </summary>
    private async Task TrySendStickerAsync(ChatState state, string replyText, string? replyToMessageId,
        string idemKey, EffectiveConfig cfg)
    {
        if (_stickers is null || !cfg.StickerEnabled)
        {
            return;
        }

        StickerScore? best = null;
        var roll = 0d;
        var probability = cfg.StickerProbability;
        var mode = string.IsNullOrWhiteSpace(replyToMessageId) ? "active" : "passive";

        try
        {
            var now = _clock.UtcNow;

            if (cfg.StickerCooldownSec > 0
                && _stickers.LastSentAt(state.ChatId) is { } lastSent
                && now - lastSent < TimeSpan.FromSeconds(cfg.StickerCooldownSec))
            {
                return;
            }

            var stickers = _stickers.List(enabledOnly: true);
            if (stickers.Count == 0)
            {
                return;
            }

            best = StickerMatch.Best(StickerMatch.Score(replyText, stickers, cfg.StickerMinWeight));
            if (best is null)
            {
                return;
            }

            if (mode == "active" && !cfg.StickerActiveWhenNoMsg)
            {
                LogSticker(best, 0, "skipped_active", mode);
                _log.LogInformation("[{Chat}] 表情包跳过：这一轮没有可被动回复的 msg_id，且主动发送已关闭",
                    Mask(state.ChatId));
                return;
            }

            roll = StickerRoll();
            if (roll >= probability)
            {
                LogSticker(best, roll, "skipped_probability", mode);
                return;
            }

            var path = _stickers.ResolvePath(best.Sticker);
            if (path is null)
            {
                LogSticker(best, roll, "failed", mode);
                _log.LogWarning("[{Chat}] 表情包 #{Id} 的图片文件不见了，已跳过",
                    Mask(state.ChatId), best.Sticker.StickerId);
                return;
            }

            // 和上一条气泡之间留一点空隙，别像同时甩出来的。
            var gap = Delivery.BubbleGap(cfg);
            if (gap > TimeSpan.Zero)
            {
                await Task.Delay(gap, _cts.Token).ConfigureAwait(false);
            }

            var outgoing = new OutgoingMessage(
                state.ChatId,
                state.Kind,
                string.Empty,
                null,
                replyToMessageId,
                $"{idemKey}:sticker",
                new OutgoingSticker(best.Sticker.StickerId, path, best.Sticker.ContentType, best.Sticker.OriginalName));

            await _adapter.SendAsync(outgoing, _cts.Token).ConfigureAwait(false);

            _stickers.MarkSent(best.Sticker.StickerId, now);
            LogSticker(best, roll, "sent", mode);

            _log.LogInformation(
                "[{Chat}] 表情包 #{Id} 已发送（得分 {Score}/{Threshold}，roll {Roll:F2} < {Probability:F2}，{Mode}）",
                Mask(state.ChatId), best.Sticker.StickerId, best.Score, best.Threshold, roll, probability,
                mode == "passive" ? "被动回复" : "主动消息");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Chat}] 表情包发送失败（正文已正常发出）", Mask(state.ChatId));
            if (best is not null)
            {
                LogSticker(best, roll, "failed", mode);
            }
        }

        void LogSticker(StickerScore score, double rollValue, string result, string sendMode)
        {
            _stickers!.Log(new StickerAttempt(
                state.ChatId,
                score.Sticker.StickerId,
                score.Sticker.Keywords,
                score.Score,
                score.Threshold,
                probability,
                rollValue,
                result,
                sendMode,
                ClipForLog(replyText, 80),
                _clock.UtcNow));
        }
    }

    private static string ClipForLog(string text, int limit)
    {
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }

    // ------------------------------------------------------------------ 轮换

    private async Task CheckRotationsAsync()
    {
        var now = _clock.UtcNow;
        foreach (var state in _chats.Values)
        {
            var session = state.Session;
            if (session is null || session.State != SessionState.Active)
            {
                continue;
            }

            bool hasPending;
            lock (state.Sync)
            {
                hasPending = state.Pending.Count > 0;
            }

            if (hasPending)
            {
                continue; // 有消息待回：先回完再轮换（文档 3.3.3）
            }

            var cfg = _config.ForChat(state.ChatId);
            if (!string.Equals(state.Fingerprint, Fingerprint(cfg), StringComparison.Ordinal))
            {
                await RotateAsync(state, RotationReason.Manual, "破坏缓存的配置发生变更").ConfigureAwait(false);
                continue;
            }

            var historyTokens = _estimator.EstimateMessages(session.Turns.Select(t => t.Content));
            var check = _sessions.Check(session, cfg, historyTokens, now);
            if (check.ShouldRotate)
            {
                await RotateAsync(state, check.Reason, check.Detail).ConfigureAwait(false);
            }
        }
    }

    private async Task RotateAsync(ChatState state, RotationReason reason, string detail)
    {
        await state.Gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            var session = state.Session;
            if (session is null || session.State != SessionState.Active)
            {
                return;
            }

            var cfg = _config.ForChat(state.ChatId);
            _log.LogInformation("[{Chat}] 轮换开启（{Reason}）：{Detail}", Mask(state.ChatId), reason, detail);

            if (session.Turns.Count == 0)
            {
                CloseSession(state, session, reason);
                return;
            }

            session.BeginSummarizing();
            await SummarizeAsync(state, session, cfg).ConfigureAwait(false);
            CloseSession(state, session, reason);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Chat}] 轮换失败", Mask(state.ChatId));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task SummarizeAsync(ChatState state, Session session, EffectiveConfig cfg)
    {
        var model = cfg.SummaryModel.Length > 0 ? cfg.SummaryModel : cfg.Model;
        var messages = new List<PromptMessage>(session.Turns.Count + 2)
        {
            new("system", session.PrefixText ?? string.Empty),
        };
        messages.AddRange(session.Turns.Select(t => new PromptMessage(t.Role, t.Content)));
        messages.Add(new PromptMessage("user", SummaryParser.Template));

        var requestId = Guid.NewGuid().ToString("N")[..16];
        var maxTokens = Math.Max(256, _config.Raw.Llm.MaxOutputTokens);

        _requestLog.Insert(new RequestLogRow(
            requestId, session.SessionId, state.ChatId, 0, "summary", model, TimeUtil.NowUnix(),
            session.PrefixHash ?? string.Empty, session.PrefixTokens, null, null, null, null, null, null, null,
            false, null, null, false, null));

        var result = await _models.Get(_config.Raw.Llm).CompleteAsync(
            new ChatCompletionRequest(model, messages, Math.Max(0.1, cfg.Temperature * 0.7), maxTokens, cfg.Thinking),
            _cts.Token).ConfigureAwait(false);

        if (!result.Ok)
        {
            _log.LogWarning("[{Chat}] 总结请求失败 {Code}，写入 WAL", Mask(state.ChatId), result.ErrorCode);
            _wal.Add(session.SessionId, WalStore.KindSummary,
                System.Text.Json.JsonSerializer.Serialize(new { chatId = state.ChatId, error = result.ErrorCode }));
            return;
        }

        var parsed = SummaryParser.Parse(result.Text);
        var attempts = 0;
        while (!parsed.HasOps && !parsed.IsNone && attempts < cfg.RetryOnParseFail)
        {
            attempts++;
            _log.LogWarning("[{Chat}] 总结格式违规，重试 {N}", Mask(state.ChatId), attempts);

            var retryMessages = new List<PromptMessage>(messages)
            {
                new("assistant", result.Text ?? string.Empty),
                new("user", "格式不合格：每行必须以 NEW_PERSON / UPDATE_PERSON / NEW_EVENT / NAME / PIN 开头并用 | 分隔；没有内容就只输出 NONE。"),
            };

            result = await _models.Get(_config.Raw.Llm).CompleteAsync(
                new ChatCompletionRequest(model, retryMessages, Math.Max(0.1, cfg.Temperature * 0.5), maxTokens, cfg.Thinking),
                _cts.Token).ConfigureAwait(false);

            if (!result.Ok)
            {
                break;
            }

            parsed = SummaryParser.Parse(result.Text);
        }

        _db.Exec(
            "UPDATE request_log SET parse_ok=@ok, prompt_tokens=@p, cache_hit_tokens=@h, completion_tokens=@c, latency_ms=@lat WHERE request_id=@id",
            ("@ok", parsed.HasOps || parsed.IsNone ? 1 : 0),
            ("@p", result.Usage.PromptTokens),
            ("@h", result.Usage.CacheHitTokens),
            ("@c", result.Usage.CompletionTokens),
            ("@lat", result.LatencyMs),
            ("@id", requestId));

        if (!parsed.HasOps && !parsed.IsNone)
        {
            _log.LogError("[{Chat}] 总结解析失败，原文入 WAL（kind=summary_raw）", Mask(state.ChatId));
            _wal.Add(session.SessionId, WalStore.KindSummaryRaw,
                System.Text.Json.JsonSerializer.Serialize(new { chatId = state.ChatId, raw = result.Text }));
            return;
        }

        if (parsed.IsNone)
        {
            _log.LogInformation("[{Chat}] 本轮无新增记忆（NONE）", Mask(state.ChatId));
        }
        else
        {
            var labelToOpenId = state.Labels
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

            var applied = _summaryApplier.Apply(state.ChatId, session.SessionId, parsed, cfg, labelToOpenId);
            _log.LogInformation("[{Chat}] 记忆入库 batch={Batch} 成功 {Applied} 跳过 {Skipped}",
                Mask(state.ChatId), applied.BatchId, applied.Applied, applied.Skipped);

            foreach (var warning in applied.Warnings)
            {
                _log.LogWarning("[{Chat}] 入库告警：{Warning}", Mask(state.ChatId), warning);
            }
        }

        var rolling = BuildRollingSummary(session.Turns);
        if (rolling.Length > 0)
        {
            state.LastSummary = rolling;
            PersistChatState(state.ChatId, rolling, state.CooldownUntil);
        }
    }

    private void CloseSession(ChatState state, Session session, RotationReason reason)
    {
        session.Close(reason, _clock.UtcNow);
        _sessions.MarkClosed(session);
        _identity.ResetSession(state.ChatId);

        state.Session = null;
        state.Seq = 0;
        state.Fingerprint = null;

        // 会话已经结束，之前攒的"未读"通通作废——留着只会在下次被叫时冒充当前对话。
        lock (state.Sync)
        {
            state.Unseen.Clear();
        }

        // 标签保留：IdentityService 的缓存已经在上一行被重置，下次该人发言时会重新解析出最新标签。
        // 清空反而会让"总结期间缓冲的消息"在新会话里丢掉发言人身份。

        lock (state.Sync)
        {
            if (state.SummarizingBuffer.Count > 0)
            {
                var buffer = state.SummarizingBuffer.ToArray();
                state.SummarizingBuffer.Clear();
                state.Pending.InsertRange(0, buffer);
                state.FirstPendingAt ??= buffer[0].Timestamp;
                state.LastMessageAt ??= buffer[^1].Timestamp;
            }
        }

        _log.LogInformation("[{Chat}] 会话 {Session} 关闭（{Reason}），上下文已释放",
            Mask(state.ChatId), session.SessionId, reason);
    }

    /// <summary>
    /// "上轮摘要"不额外花一次请求：从最后几轮对话里本地压一句，60 字。换连续性，成本为零。
    /// </summary>
    private static string BuildRollingSummary(IReadOnlyList<SessionTurn> turns)
    {
        var lastAssistant = turns.LastOrDefault(t => t.Role == "assistant" && t.Kind == TurnKind.History)?.Content;
        var lastUserTurn = turns.LastOrDefault(t => t.Role == "user" && t.Kind == TurnKind.DynamicTail)?.Content;

        var lastUserMessages = string.Empty;
        if (!string.IsNullOrEmpty(lastUserTurn))
        {
            var index = lastUserTurn.IndexOf("【消息】", StringComparison.Ordinal);
            lastUserMessages = index >= 0 ? lastUserTurn[(index + 4)..].Trim() : lastUserTurn;
        }

        var builder = new StringBuilder();
        if (lastUserMessages.Length > 0)
        {
            builder.Append("聊到").Append(Trim(lastUserMessages, 30));
        }

        if (!string.IsNullOrWhiteSpace(lastAssistant))
        {
            if (builder.Length > 0)
            {
                builder.Append('，');
            }

            builder.Append("我回了").Append(Trim(lastAssistant, 24));
        }

        return Trim(builder.ToString(), 60);
    }

    private static string Trim(string text, int limit)
    {
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }

    // ------------------------------------------------------------------ 恢复

    private async Task RecoverAsync(CancellationToken token)
    {
        foreach (var item in _wal.Pending())
        {
            try
            {
                switch (item.Kind)
                {
                    case WalStore.KindSend:
                        // 已生成未确认：绝不重发（文档 3.2.3 第 2 条）。
                        _wal.Remove(item.Id);
                        _log.LogInformation("丢弃未确认的发送意图，避免重复发送");
                        break;

                    case WalStore.KindSummaryRaw:
                        ReplaySummaryRaw(item);
                        break;

                    default:
                        _log.LogInformation("WAL 项 {Kind} 待后台重试", item.Kind);
                        break;
                }
            }
            catch (Exception ex)
            {
                _wal.Fail(item.Id, ex.Message);
            }
        }

        foreach (var (sessionId, chatId) in _sessions.ListOrphans())
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            var session = _sessions.TryResume(sessionId);
            if (session is null)
            {
                continue;
            }

            _log.LogInformation("崩溃恢复：会话 {Session} 降级为 SUMMARIZING 补总结", sessionId);
            var state = _chats.GetOrAdd(chatId, id => new ChatState(id));
            state.Session = session;
            await RotateAsync(state, RotationReason.Error, "进程重启后补总结").ConfigureAwait(false);
        }
    }

    private void ReplaySummaryRaw(WalItem item)
    {
        using var payload = System.Text.Json.JsonDocument.Parse(item.Payload);
        var chatId = payload.RootElement.TryGetProperty("chatId", out var chatElement) ? chatElement.GetString() : null;
        var raw = payload.RootElement.TryGetProperty("raw", out var rawElement) ? rawElement.GetString() : null;
        if (chatId is null || raw is null)
        {
            _wal.Remove(item.Id);
            return;
        }

        var parsed = SummaryParser.Parse(raw);
        if (!parsed.HasOps)
        {
            _wal.Fail(item.Id, "仍无法解析");
            return;
        }

        var cfg = _config.ForChat(chatId);
        var applied = _summaryApplier.Apply(chatId, item.SessionId, parsed, cfg, new Dictionary<string, string>());
        _log.LogInformation("WAL 重放总结：batch={Batch} 成功 {Applied}", applied.BatchId, applied.Applied);
        _wal.Remove(item.Id);
    }

    // ------------------------------------------------------------------ 辅助

    private void AppendTurn(ChatState state, Session session, SessionTurn turn)
    {
        session.AppendTurn(turn);
        _sessions.SaveTurn(session, turn, session.Turns.Count);
    }

    private void BufferPending(ChatState state, IReadOnlyList<IncomingMessage> batch)
    {
        var cfg = _config.ForChat(state.ChatId);
        lock (state.Sync)
        {
            var room = Math.Max(0, cfg.PendingBufferMax - state.SummarizingBuffer.Count);
            state.SummarizingBuffer.AddRange(batch.Take(room));
        }

        _log.LogInformation("[{Chat}] 总结期间收到 {Count} 条消息，进入 pending_buffer", Mask(state.ChatId), batch.Count);
    }

    private void SaveMessage(IncomingMessage message, string label)
    {
        var id = message.MessageId ?? Guid.NewGuid().ToString("N")[..20];
        _db.Exec(
            """
            INSERT OR REPLACE INTO message(message_id, chat_id, open_id, sender_label, content, at, mentions_bot, reply_to)
            VALUES(@id, @chat, @open, @label, @content, @at, @mention, @reply)
            """,
            ("@id", id),
            ("@chat", message.ChatId),
            ("@open", message.OpenId),
            ("@label", label),
            ("@content", message.Content),
            ("@at", TimeUtil.Unix(message.Timestamp)),
            ("@mention", message.MentionsBot ? 1 : 0),
            ("@reply", message.ReplyToMessageId));
    }

    private void PersistChatState(string chatId, string? summary, DateTimeOffset? cooldownUntil)
    {
        _db.Exec(
            """
            INSERT INTO chat_state(chat_id, last_summary, cooldown_until, updated_at)
            VALUES(@c, @s, @u, @t)
            ON CONFLICT(chat_id) DO UPDATE SET last_summary=@s, cooldown_until=@u, updated_at=@t
            """,
            ("@c", chatId),
            ("@s", summary ?? string.Empty),
            ("@u", cooldownUntil is null ? null : TimeUtil.Unix(cooldownUntil.Value)),
            ("@t", TimeUtil.NowUnix()));
    }

    private string? ResolveMention(ChatState state, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var person = _identity.ResolveByLabel(state.ChatId, label);
        return person?.OpenId;
    }

    /// <summary>调用方必须已持有 state.Sync。</summary>
    private string LabelOfLocked(ChatState state, IncomingMessage message)
    {
        if (state.Labels.TryGetValue(message.OpenId, out var label))
        {
            return label;
        }

        return message.IsSelf ? _config.Raw.Persona.BotName : "群友??";
    }

    private string Fingerprint(EffectiveConfig cfg)
    {
        var persona = _config.Raw.Persona;
        var builder = new StringBuilder(512);
        builder.Append(cfg.Model).Append('|')
            .Append(persona.BotName).Append('|')
            .Append(persona.Persona).Append('|')
            .Append(persona.ExtraRules);

        foreach (var spec in ParamRegistry.All.Where(s => s.BreaksCache))
        {
            builder.Append(';').Append(spec.Name).Append('=').Append(cfg.Params.GetNode(spec.Name)?.ToJsonString());
        }

        return PromptHasher.Compute(builder.ToString());
    }

    private bool IsPaused(string chatId)
    {
        var settings = _config.Raw;
        return settings.Chats.TryGetValue(chatId, out var chat) && chat.Paused;
    }

    private static string Mask(string chatId) =>
        chatId.Length <= 10 ? chatId : chatId[..6] + "…" + chatId[^4..];

    /// <summary>空档案（只有 open_id 映射、没有名字/性格/评价/记忆）没有注入价值。</summary>
    private static bool HasProfile(PersonRow person) =>
        !string.IsNullOrWhiteSpace(person.Name)
        || !string.IsNullOrWhiteSpace(person.Personality)
        || !string.IsNullOrWhiteSpace(person.Evaluation)
        || !string.IsNullOrWhiteSpace(person.MemoryText);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private sealed class ChatState
    {
        public ChatState(string chatId) => ChatId = chatId;

        public string ChatId { get; }

        public object Sync { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ChatKind Kind { get; set; } = ChatKind.Group;

        public string? DisplayName { get; set; }

        /// <summary>平台事件推出来的接收模式：true=全量，false=仅 @，null=还没见过事件。</summary>
        public bool? ReceivesAllGroupMessages { get; set; }

        public Session? Session { get; set; }

        public List<IncomingMessage> Pending { get; } = new();

        public List<IncomingMessage> Recent { get; } = new();

        public List<IncomingMessage> SummarizingBuffer { get; } = new();

        /// <summary>自上次成功回复以来、还没进过上下文的群聊发言。</summary>
        public List<IncomingMessage> Unseen { get; } = new();

        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? FirstPendingAt { get; set; }

        public DateTimeOffset? LastMessageAt { get; set; }

        public DateTimeOffset? CooldownUntil { get; set; }

        /// <summary>下一次"主动接话"的时间点（active_tick_sec 开启时才有意义）。</summary>
        public DateTimeOffset? NextCadenceAt { get; set; }

        /// <summary>当前会话前缀里已经包含的人/事。</summary>
        public IReadOnlyList<int> PrefixPersonIds { get; set; } = Array.Empty<int>();

        public IReadOnlyList<long> PrefixEventIds { get; set; } = Array.Empty<long>();

        /// <summary>已经通过「按需补充」补进对话尾部的人/事（每会话一次）。</summary>
        public HashSet<int> TailPersonIds { get; } = new();

        public HashSet<long> TailEventIds { get; } = new();

        public int ConsecutiveUnanswered { get; set; }

        public string LastSummary { get; set; } = string.Empty;

        public string? Fingerprint { get; set; }

        public int Seq { get; set; }
    }
}

public sealed record ChatSnapshot(
    string ChatId,
    ChatKind Kind,
    string? DisplayName,
    string? SessionId,
    SessionState State,
    int TurnCount,
    int PendingCount,
    DateTimeOffset? CooldownUntil,
    string? PrefixHash,
    DateTimeOffset? LastActivityAt,
    bool? ReceivesAllGroupMessages);

/// <summary>按当前 API Key / 地址缓存客户端，配置页改了 key 不必重启进程。</summary>
public sealed class ModelProvider : IDisposable
{
    private readonly ILogger<DeepSeekChatModel>? _log;
    private readonly Func<LlmSettings, IChatModel> _factory;
    private readonly object _gate = new();
    private (string BaseUrl, string Key, int Timeout, IChatModel Model)? _cached;

    public ModelProvider(ILogger<DeepSeekChatModel>? log = null, Func<LlmSettings, IChatModel>? factory = null)
    {
        _log = log;
        _factory = factory ?? (settings => new DeepSeekChatModel(settings, _log));
    }

    public IChatModel Get(LlmSettings settings)
    {
        lock (_gate)
        {
            if (_cached is { } cached
                && cached.BaseUrl == settings.BaseUrl
                && cached.Key == settings.ApiKey
                && cached.Timeout == settings.TimeoutSeconds)
            {
                return cached.Model;
            }

            var model = _factory(settings);
            _cached = (settings.BaseUrl, settings.ApiKey, settings.TimeoutSeconds, model);
            return model;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            (_cached?.Model as IDisposable)?.Dispose();
            _cached = null;
        }
    }
}
