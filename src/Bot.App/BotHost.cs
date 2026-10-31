using Bot.Core;
using Bot.Core.Abstractions;
using Bot.Core.Config;
using Bot.Core.Memory;
using Bot.Core.Runtime;
using Bot.Core.Text;
using Bot.QQ;
using Microsoft.Extensions.Logging;

namespace Bot.App;

/// <summary>
/// 组装并托管整个机器人：数据库 → 记忆 → 运行时 → 适配器 → 本地配置页。
/// <para>切换适配器（mock ↔ qq）需要重启进程；其余设置可以热更新。</para>
/// </summary>
public sealed class BotHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ModelProvider _models;
    private AppSettings _settings = new();
    private bool _started;

    public BotHost()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(AppPaths.LogDirectory));
        });

        Log = LoggerFactory.CreateLogger<BotHost>();
        _models = new ModelProvider(LoggerFactory.CreateLogger<Bot.Core.Llm.DeepSeekChatModel>());
        Db = new Db(AppPaths.DbFile);
    }

    public ILoggerFactory LoggerFactory { get; }

    public ILogger<BotHost> Log { get; }

    public Db Db { get; }

    public ConfigService Config { get; private set; } = new(new AppSettings());

    public TokenEstimator Estimator { get; } = new();

    /// <summary>三区组装器：运行时与调参页的提示词预览共用同一套规则。</summary>
    public PromptAssembler Assembler { get; private set; } = null!;

    /// <summary>只读：按当前参数渲染提示词框架（调参页用，不读取真实会话）。</summary>
    public PromptFrameworkService Framework { get; private set; } = null!;

    public MemoryOpLog OpLog { get; private set; } = null!;

    public IdentityService Identity { get; private set; } = null!;

    public EventStore Events { get; private set; } = null!;

    public RetrievalService Retrieval { get; private set; } = null!;

    public SessionManager Sessions { get; private set; } = null!;

    /// <summary>表情包图片库（图片落盘 + 元数据入库）。</summary>
    public StickerStore Stickers { get; private set; } = null!;

    /// <summary>只读：把某个会话当时发给 API 的 messages 还原出来（配置页"当前会话"用）。</summary>
    public SessionInspector Inspector { get; private set; } = null!;

    public WalStore Wal { get; private set; } = null!;

    public ChatRuntime Runtime { get; private set; } = null!;

    public IChatAdapter Adapter { get; private set; } = null!;

    public WebServer? Web { get; private set; }

    public ModelProvider Models => _models;

    public MockChatAdapter? MockAdapter => Adapter as MockChatAdapter;

    public AppSettings Settings => _settings;

    public string AdapterName => Adapter?.Name ?? "none";

    public async Task StartAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);

        _settings = SettingsStore.Load(AppPaths.SettingsFile, out var loadError);
        if (loadError is not null)
        {
            Log.LogWarning(loadError);
        }

        Config = new ConfigService(_settings);
        foreach (var warning in Config.Warnings)
        {
            Log.LogWarning("配置告警：{Warning}", warning);
        }

        Db.Initialize();

        OpLog = new MemoryOpLog(Db);
        Identity = new IdentityService(Db, Config, SystemClock.Instance, OpLog);
        var clearedNames = Identity.CleanupDuplicateNames();
        if (clearedNames > 0)
        {
            Log.LogInformation("启动清理：{Count} 个重名人物已清掉名字（保留 person_id 更小的）", clearedNames);
        }

        Events = new EventStore(Db, SystemClock.Instance, OpLog, LoggerFactory.CreateLogger<EventStore>());
        Retrieval = new RetrievalService(Events, new BigramTokenizer(), LoggerFactory.CreateLogger<RetrievalService>());
        Sessions = new SessionManager(Db, SystemClock.Instance, LoggerFactory.CreateLogger<SessionManager>());
        Stickers = new StickerStore(Db, AppPaths.StickerDirectory);
        Wal = new WalStore(Db);
        Inspector = new SessionInspector(Db, Sessions, new RequestLogStore(Db), Estimator);
        Assembler = new PromptAssembler(Estimator);
        Framework = new PromptFrameworkService(Config, Assembler, Estimator);

        var summaryApplier = new SummaryApplier(
            Db, Identity, Events, OpLog, SystemClock.Instance, LoggerFactory.CreateLogger<SummaryApplier>());

        Adapter = CreateAdapter();
        Runtime = new ChatRuntime(
            Db,
            SystemClock.Instance,
            Config,
            Identity,
            Events,
            Retrieval,
            Assembler,
            Sessions,
            new RequestLogStore(Db),
            Wal,
            summaryApplier,
            Estimator,
            Adapter,
            _models,
            LoggerFactory.CreateLogger<ChatRuntime>(),
            stickers: Stickers);

        Adapter.MessageReceived += message => Runtime.EnqueueAsync(message);

        await Adapter.StartAsync(_cts.Token).ConfigureAwait(false);
        await Runtime.StartAsync(_cts.Token).ConfigureAwait(false);

        Web = new WebServer(this, _settings.Server);
        await Web.StartAsync().ConfigureAwait(false);

        _started = true;

        Log.LogInformation("机器人已启动｜适配器={Adapter}｜数据目录={Root}", AdapterName, AppPaths.Root);
    }

    public void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        SettingsStore.Save(AppPaths.SettingsFile, settings);
        Config.Update(settings);
    }

    public void RebuildSettings(Action<AppSettings> mutate)
    {
        mutate(_settings);
        SaveSettings(_settings);
    }

    private IChatAdapter CreateAdapter()
    {
        var qq = _settings.Qq;
        var logger = LoggerFactory.CreateLogger<QqChatAdapter>();

        if (string.Equals(qq.Adapter, "qq", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(qq.AppId) || string.IsNullOrWhiteSpace(qq.Secret))
            {
                Log.LogWarning("QQ 适配器缺少 AppId / Secret，本次使用 Mock 适配器");
                return new MockChatAdapter();
            }

            return new QqChatAdapter(qq, logger, null, LoggerFactory.CreateLogger<Bot.QQ.QqGateway>());
        }

        return new MockChatAdapter();
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await Runtime.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "停止运行时出错");
        }

        if (Web is not null)
        {
            try
            {
                await Web.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "停止配置页出错");
            }
        }

        await Adapter.DisposeAsync().ConfigureAwait(false);
        _models.Dispose();
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        LoggerFactory.Dispose();
        Db.Dispose();
        _cts.Dispose();
    }
}
