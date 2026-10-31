using Bot.Core.Abstractions;
using Bot.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Bot.Core.Runtime;

/// <summary>新增 / 修改表情包时提交的内容。图片为空表示保留原图（仅修改关键词等）。</summary>
public sealed record StickerInput(
    string Keywords,
    bool Enabled,
    byte[]? Image = null,
    string? OriginalName = null);

/// <summary>一次命中判定（sent / skipped_probability / skipped_active / failed），写进 sticker_log。</summary>
public sealed record StickerAttempt(
    string ChatId,
    int StickerId,
    string Keywords,
    double Score,
    double Threshold,
    double Probability,
    double Roll,
    string Result,
    string Mode,
    string ReplyExcerpt,
    DateTimeOffset At);

public sealed record StickerLogRow(
    long Id,
    string ChatId,
    int StickerId,
    string Keywords,
    double Score,
    double Threshold,
    double Probability,
    double Roll,
    string Result,
    string Mode,
    string ReplyExcerpt,
    long CreatedAt);

/// <summary>
/// 表情包仓库：图片落盘（<c>data\stickers\</c>）+ 元数据存库。
/// <para>
/// 图片格式按文件头校验，不信任扩展名；单张上限 4MB（QQ 富媒体直传上限 5MB，留余量）。
/// 表情包只写 <c>sticker_log</c>，绝不写 <c>message</c> / <c>session_turn</c>，因此不会进入对话历史。
/// </para>
/// </summary>
public sealed class StickerStore
{
    /// <summary>QQ 富媒体「直接上传」上限 5MB；这里留 1MB 余量。</summary>
    public const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>sticker_log 只保留最近这么多条，避免无限增长。</summary>
    public const int MaxLogRows = 2000;

    private const string SelectColumns =
        "sticker_id, file_name, original_name, content_type, keywords, enabled, " +
        "send_count, last_sent_at, created_at, updated_at";

    private readonly Db _db;
    private readonly string _directory;

    public StickerStore(Db db, string directory)
    {
        _db = db;
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>图片目录的绝对路径（配置页排查用）。</summary>
    public string DirectoryPath => _directory;

    public IReadOnlyList<StickerRow> List(bool enabledOnly = false) =>
        _db.Query(
            $"SELECT {SelectColumns} FROM sticker{(enabledOnly ? " WHERE enabled=1" : string.Empty)} ORDER BY sticker_id",
            MapRow);

    public StickerRow? Get(int stickerId) =>
        _db.QueryOne($"SELECT {SelectColumns} FROM sticker WHERE sticker_id=@id", MapRow, ("@id", stickerId));

    public StickerRow Create(StickerInput input)
    {
        var (keywords, enabled) = Validate(input);
        if (input.Image is not { Length: > 0 })
        {
            throw new ArgumentException("请选择一张图片");
        }

        var image = ValidateImage(input.Image);
        var fileName = NewFileName(image.Extension);
        WriteFile(fileName, input.Image);

        try
        {
            var id = (int)_db.ScalarLong(
                """
                INSERT INTO sticker(file_name, original_name, content_type, keywords, enabled,
                                    send_count, created_at, updated_at)
                VALUES(@file, @original, @type, @keywords, @enabled, 0, @now, @now)
                RETURNING sticker_id
                """,
                ("@file", fileName),
                ("@original", input.OriginalName ?? string.Empty),
                ("@type", image.ContentType),
                ("@keywords", keywords),
                ("@enabled", enabled ? 1 : 0),
                ("@now", TimeUtil.NowUnix()));

            return Get(id) ?? throw new InvalidOperationException("表情包写入后读取失败");
        }
        catch
        {
            DeleteFile(fileName);
            throw;
        }
    }

    public StickerRow Update(int stickerId, StickerInput input)
    {
        var existing = Get(stickerId) ?? throw new ArgumentException("表情包不存在");
        var (keywords, enabled) = Validate(input);

        StickerImage? image = input.Image is { Length: > 0 } ? ValidateImage(input.Image) : null;
        string? newFile = null;
        if (image is not null)
        {
            newFile = NewFileName(image.Extension);
            WriteFile(newFile, input.Image!);
        }

        try
        {
            _db.Exec(
                """
                UPDATE sticker SET
                  file_name     = COALESCE(@file, file_name),
                  original_name = CASE WHEN @file IS NULL THEN original_name ELSE @original END,
                  content_type  = COALESCE(@type, content_type),
                  keywords      = @keywords,
                  enabled       = @enabled,
                  updated_at    = @now
                WHERE sticker_id = @id
                """,
                ("@file", newFile),
                ("@original", input.OriginalName ?? string.Empty),
                ("@type", image?.ContentType),
                ("@keywords", keywords),
                ("@enabled", enabled ? 1 : 0),
                ("@now", TimeUtil.NowUnix()),
                ("@id", stickerId));
        }
        catch
        {
            if (newFile is not null)
            {
                DeleteFile(newFile);
            }

            throw;
        }

        if (newFile is not null)
        {
            DeleteFile(existing.FileName);
        }

        return Get(stickerId) ?? throw new InvalidOperationException("表情包更新后读取失败");
    }

    public bool SetEnabled(int stickerId, bool enabled) =>
        _db.Exec("UPDATE sticker SET enabled=@e, updated_at=@now WHERE sticker_id=@id",
            ("@e", enabled ? 1 : 0),
            ("@now", TimeUtil.NowUnix()),
            ("@id", stickerId)) > 0;

    public bool Delete(int stickerId)
    {
        var existing = Get(stickerId);
        if (existing is null)
        {
            return false;
        }

        var removed = _db.Exec("DELETE FROM sticker WHERE sticker_id=@id", ("@id", stickerId)) > 0;
        if (removed)
        {
            DeleteFile(existing.FileName);
        }

        return removed;
    }

    /// <summary>读取图片本体（配置页预览 / 本地试聊显示用）。文件缺失时返回 null。</summary>
    public (byte[] Bytes, string ContentType)? ReadImage(int stickerId)
    {
        var row = Get(stickerId);
        if (row is null)
        {
            return null;
        }

        var path = ResolvePath(row);
        if (path is null)
        {
            return null;
        }

        try
        {
            return (File.ReadAllBytes(path), row.ContentType);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>图片的绝对路径；文件不存在或路径越界时返回 null。</summary>
    public string? ResolvePath(StickerRow row)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(_directory, row.FileName));
            var root = _directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 记一次成功发送：使用次数 +1、最近发送时间更新。
    /// <para>不动 <c>updated_at</c>：那是图片/关键词的版本号，发送不该让图片缓存 URL 抖动。</para>
    /// </summary>
    public void MarkSent(int stickerId, DateTimeOffset at) =>
        _db.Exec(
            "UPDATE sticker SET send_count=send_count+1, last_sent_at=@at WHERE sticker_id=@id",
            ("@at", TimeUtil.Unix(at)),
            ("@id", stickerId));

    public void Log(StickerAttempt attempt)
    {
        _db.Exec(
            """
            INSERT INTO sticker_log(chat_id, sticker_id, keywords, score, threshold, probability, roll,
                                    result, mode, reply_excerpt, created_at)
            VALUES(@chat, @id, @keywords, @score, @threshold, @probability, @roll,
                   @result, @mode, @excerpt, @at)
            """,
            ("@chat", attempt.ChatId),
            ("@id", attempt.StickerId),
            ("@keywords", attempt.Keywords),
            ("@score", Math.Round(attempt.Score, 2)),
            ("@threshold", attempt.Threshold),
            ("@probability", attempt.Probability),
            ("@roll", Math.Round(attempt.Roll, 4)),
            ("@result", attempt.Result),
            ("@mode", attempt.Mode),
            ("@excerpt", attempt.ReplyExcerpt),
            ("@at", TimeUtil.Unix(attempt.At)));

        _db.Exec("DELETE FROM sticker_log WHERE id <= (SELECT MAX(id) FROM sticker_log) - @keep",
            ("@keep", MaxLogRows));
    }

    public IReadOnlyList<StickerLogRow> RecentLog(int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 500);
        return _db.Query(
            """
            SELECT id, chat_id, sticker_id, keywords, score, threshold, probability, roll,
                   result, mode, reply_excerpt, created_at
            FROM sticker_log ORDER BY id DESC LIMIT @n
            """,
            reader => new StickerLogRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11)),
            ("@n", limit));
    }

    /// <summary>这个聊天上一次真正发出表情包的时间（冷却判断用）。</summary>
    public DateTimeOffset? LastSentAt(string chatId)
    {
        var value = _db.ScalarLong(
            "SELECT COALESCE(MAX(created_at), 0) FROM sticker_log WHERE chat_id=@c AND result='sent'",
            ("@c", chatId));
        return value <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    // ------------------------------------------------------------------ 校验与文件

    private static (string Keywords, bool Enabled) Validate(StickerInput input)
    {
        var keywords = input.Keywords?.Trim() ?? string.Empty;
        if (StickerMatch.ParseKeywords(keywords).Count == 0)
        {
            throw new ArgumentException("至少填一个关键词");
        }

        return (keywords, input.Enabled);
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static StickerImage ValidateImage(byte[] bytes)
    {
        if (bytes.Length > MaxImageBytes)
        {
            throw new ArgumentException($"图片太大了：{bytes.Length / 1024d / 1024d:F1}MB，上限 4MB");
        }

        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            return new StickerImage("image/png", ".png");
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return new StickerImage("image/jpeg", ".jpg");
        }

        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return new StickerImage("image/gif", ".gif");
        }

        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return new StickerImage("image/webp", ".webp");
        }

        throw new ArgumentException("只支持 png / jpg / gif / webp 图片");
    }

    private string NewFileName(string extension) => Guid.NewGuid().ToString("N") + extension;

    private void WriteFile(string fileName, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_directory, fileName), bytes);

    private void DeleteFile(string fileName)
    {
        try
        {
            var path = Path.Combine(_directory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删除失败不影响数据一致性：DB 已经指不到它了，最多留个孤儿文件。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static StickerRow MapRow(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5) == 1,
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.GetInt64(8),
        reader.GetInt64(9));

    private sealed record StickerImage(string ContentType, string Extension);
}
