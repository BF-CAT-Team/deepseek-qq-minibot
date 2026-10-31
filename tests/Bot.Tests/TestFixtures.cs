using System.Text.Json.Nodes;
using Bot.Core;
using Bot.Core.Config;
using Bot.Core.Domain;
using Bot.Core.Llm;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Bot.Core.Text;
using Bot.QQ;

namespace Bot.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 21, 0, 0, TimeSpan.FromHours(8));

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
}

public sealed class FakeChatModel : IChatModel
{
    public Func<ChatCompletionRequest, string> Reply { get; set; } = _ => "好啊，那就这么定了。";

    public List<ChatCompletionRequest> Requests { get; } = new();

    public Task<LlmResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var text = Reply(request);
        var usage = new LlmUsage(1200, 1000, 200, 24);
        return Task.FromResult(new LlmResult(true, text, usage, 120, null, null));
    }
}

/// <summary>完整装配一套运行时（临时库 + Mock 适配器 + 假模型），用于回归断言。</summary>
public sealed class Harness : IAsyncDisposable
{
    public const string ChatId = "mock-group";
    public const string UserOpenId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    public Harness(string? parametersJson = null, Action<JsonObject>? tweakParameters = null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DatabasePath = Path.Combine(dir, "bot.db");

        Db = new Db(DatabasePath);
        Db.Initialize();

        var parameters = JsonNode.Parse(parametersJson ?? DefaultParameters)!.AsObject();
        tweakParameters?.Invoke(parameters);

        var settings = new AppSettings
        {
            Preset = Presets.Balanced,
            Parameters = parameters,
        };
        settings.Llm.ApiKey = "test-key";

        Config = new ConfigService(settings);
        OpLog = new MemoryOpLog(Db);
        Identity = new IdentityService(Db, Config, Clock, OpLog);
        Events = new EventStore(Db, Clock, OpLog);
        Retrieval = new RetrievalService(Events, new BigramTokenizer());
        Sessions = new SessionManager(Db, Clock);
        Stickers = new StickerStore(Db, Path.Combine(dir, "stickers"));
        Wal = new WalStore(Db);
        Logs = new RequestLogStore(Db);
        Estimator = new TokenEstimator();
        Adapter = new MockChatAdapter();
        Model = new FakeChatModel();

        var applier = new SummaryApplier(Db, Identity, Events, OpLog, Clock);
        Applier = applier;
        Runtime = new ChatRuntime(
            Db,
            Clock,
            Config,
            Identity,
            Events,
            Retrieval,
            new PromptAssembler(Estimator),
            Sessions,
            Logs,
            Wal,
            applier,
            Estimator,
            Adapter,
            new ModelProvider(factory: _ => Model),
            stickers: Stickers);

        Adapter.MessageReceived += message => Runtime.EnqueueAsync(message);
    }

    public const string DefaultParameters =
        """
        {
          "typing_jitter": false,
          "bubble_gap_min_sec": 0,
          "bubble_gap_max_sec": 0,
          "adaptive_window": false,
          "quiet_sec": 1,
          "max_wait_sec": 30,
          "event_score_threshold": 0.2,
          "prefix_drift_alert": true,
          "sticker_enabled": true,
          "sticker_probability": 1.0,
          "sticker_min_weight": 1.0,
          "sticker_cooldown_sec": 0,
          "sticker_active_when_no_msg": true
        }
        """;

    public string DatabasePath { get; }

    public FakeClock Clock { get; } = new();

    public Db Db { get; }

    public ConfigService Config { get; }

    public MemoryOpLog OpLog { get; }

    public IdentityService Identity { get; }

    public EventStore Events { get; }

    public RetrievalService Retrieval { get; }

    public SessionManager Sessions { get; }

    public StickerStore Stickers { get; }

    public WalStore Wal { get; }

    public RequestLogStore Logs { get; }

    public SummaryApplier Applier { get; }

    public TokenEstimator Estimator { get; }

    public MockChatAdapter Adapter { get; }

    public FakeChatModel Model { get; }

    public ChatRuntime Runtime { get; }

    public EffectiveConfig ConfigFor(string? chatId = null) => Config.ForChat(chatId ?? ChatId);

    /// <summary>1×1 的合法 PNG；表情包仓库只校验文件头，内容本身不影响测试。</summary>
    public static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public StickerRow AddSticker(string keywords, bool enabled = true) =>
        Stickers.Create(new StickerInput(keywords, enabled, TestPng, "test.png"));

    public async Task SendAsync(string content, string? openId = null, bool mention = true,
        string chatId = ChatId, ChatKind kind = ChatKind.Group, string? eventType = null)
    {
        await Adapter.InjectAsync(new Bot.Core.Domain.IncomingMessage
        {
            ChatId = chatId,
            Kind = kind,
            OpenId = openId ?? UserOpenId,
            Content = content,
            MentionsBot = mention,
            MessageId = Guid.NewGuid().ToString("N")[..16],
            Timestamp = Clock.UtcNow,
            ChatDisplayName = "本地测试群",
            EventType = eventType,
        });
    }

    /// <summary>发一条消息并推进到"该回已回"。</summary>
    public async Task<string?> TurnAsync(string content, string? openId = null, bool mention = true)
    {
        var before = Adapter.Sent.Count;
        await SendAsync(content, openId, mention);
        Clock.Advance(5);
        await Runtime.TickAsync();
        var sent = Adapter.Sent;
        return sent.Count > before ? string.Join(" | ", sent.Skip(before).Select(m => m.Content)) : null;
    }

    public IReadOnlyList<RequestLogRow> ChatRequests =>
        Logs.Recent(ChatId, 200).Where(r => r.Kind == "chat").OrderBy(r => r.Seq).ToList();

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await Adapter.DisposeAsync();
        Db.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(DatabasePath)!, recursive: true);
        }
        catch
        {
            // 临时目录清理失败可忽略
        }
    }
}
