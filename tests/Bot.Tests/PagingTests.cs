using Xunit;

namespace Bot.Tests;

/// <summary>
/// 配置页三张表的「最新在前 + 分页」：人物 / 事件 / 表情包命中记录。
/// 分页必须是稳定的（两页不重叠），总数必须和同一套过滤条件一致。
/// </summary>
public sealed class PagingTests
{
    [Fact]
    public async Task Persons_page_is_newest_first_and_total_matches()
    {
        await using var h = new Harness();
        for (var i = 1; i <= 25; i++)
        {
            h.Db.Exec(
                "INSERT INTO person(open_id, name, display_label, created_at, updated_at) VALUES(@o,@n,@n,@t,@t)",
                ("@o", "open-" + i),
                ("@n", "P" + i),
                ("@t", 1000 + i));
        }

        Assert.Equal(25, h.Identity.CountActive());

        var first = h.Identity.ListActivePage(0, 20);
        Assert.Equal(20, first.Count);
        Assert.Equal(25, first[0].PersonId); // 最近更新的在前

        var second = h.Identity.ListActivePage(20, 20);
        Assert.Equal(5, second.Count);
        Assert.Equal(1, second[^1].PersonId); // 第二页最后一条是最早的那个
        Assert.Empty(first.Select(p => p.PersonId).Intersect(second.Select(p => p.PersonId)));
    }

    [Fact]
    public async Task Events_page_is_newest_first_and_search_uses_the_same_filter_for_total()
    {
        await using var h = new Harness();
        for (var i = 1; i <= 12; i++)
        {
            h.Db.Exec(
                "INSERT INTO event(detail, content_hash, created_at) VALUES(@d,@h,@t)",
                ("@d", i % 2 == 0 ? "火锅" + i : "别的" + i),
                ("@h", "hash-" + i),
                ("@t", 1000 + i));
        }

        Assert.Equal(12, h.Events.Count(null));

        var page = h.Events.Page(null, 0, 5);
        Assert.Equal(5, page.Count);
        Assert.True(page[0].EventId > page[^1].EventId);

        Assert.Equal(6, h.Events.Count("火锅"));
        var search = h.Events.Page("火锅", 0, 4);
        Assert.Equal(4, search.Count);
        Assert.All(search, e => Assert.Contains("火锅", e.Detail));
    }

    [Fact]
    public async Task Sticker_log_page_is_newest_first_and_total_matches()
    {
        await using var h = new Harness();
        for (var i = 1; i <= 30; i++)
        {
            h.Db.Exec(
                """
                INSERT INTO sticker_log(chat_id, sticker_id, keywords, score, threshold, probability, roll,
                                        result, mode, reply_excerpt, created_at)
                VALUES('c', @sid, 'kw', 1, 1, 0.5, 0.1, 'sent', 'passive', '', @t)
                """,
                ("@sid", i),
                ("@t", 1000 + i));
        }

        Assert.Equal(30, h.Stickers.LogCount());

        var first = h.Stickers.LogPage(0, 20);
        Assert.Equal(20, first.Count);
        Assert.Equal(30, first[0].Id);

        var second = h.Stickers.LogPage(20, 20);
        Assert.Equal(10, second.Count);
        Assert.Equal(1, second[^1].Id);
    }
}
