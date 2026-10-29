using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Bot.App;

/// <summary>
/// 极简文件日志：按天切分，保留 14 天。WinExe 没有控制台，出问题时用户只能翻日志，
/// 所以这一块不能省。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Cleanup();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(_directory, name));

    private void Cleanup()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_directory, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    public void Dispose()
    {
        foreach (var logger in _loggers.Values)
        {
            logger.Dispose();
        }

        _loggers.Clear();
    }

    private sealed class FileLogger : ILogger, IDisposable
    {
        private static readonly object Gate = new();

        /// <summary>带 BOM 的 UTF-8：Windows 上双击用记事本打开不会乱码。</summary>
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        private readonly string _directory;
        private readonly string _category;

        public FileLogger(string directory, string category)
        {
            _directory = directory;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder(256);
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ');
            line.Append(Level(logLevel)).Append(' ');
            line.Append(Short(_category)).Append(" | ");
            line.Append(formatter(state, exception));
            if (exception is not null)
            {
                line.Append(" << ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            }

            var path = Path.Combine(_directory, $"bot-{DateTime.Now:yyyyMMdd}.log");
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(path, line.ToString() + Environment.NewLine, Utf8WithBom);
                }
            }
            catch
            {
                // 日志写入失败不能拖垮业务
            }
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };

        private static string Short(string category)
        {
            var index = category.LastIndexOf('.');
            return index >= 0 ? category[(index + 1)..] : category;
        }

        public void Dispose()
        {
        }
    }
}
