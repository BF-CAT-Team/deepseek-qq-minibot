using System.Text.Json;
using Bot.Core.Abstractions;
using Bot.Core.Domain;
using Bot.QQ;

namespace Bot.Tests;

/// <summary>
/// 载荷映射要按<b>形状</b>判断，不能死认事件名。
/// 群主开启"允许接收群内所有消息"后事件名会变，死认名字就会静默丢消息。
/// </summary>
public sealed class QqMapperTests
{
    private const string GroupPayload = """
        {"op":0,"t":"GROUP_MESSAGE_CREATE","s":42,"d":{
          "id":"MSG-1","content":"今天吃火锅吗","timestamp":"2026-10-28T17:20:00+08:00",
          "group_openid":"7CFF2FD251E3D14DC6C6F8534824A373",
          "author":{"id":"x","member_openid":"A1B2C3D4E5F60718293A4B5C6D7E8F90"}}}
        """;

    [Fact]
    public void MapsFullGroupMessage_ByShape_NotByName()
    {
        var message = Map(GroupPayload);

        Assert.NotNull(message);
        Assert.Equal(ChatKind.Group, message!.Kind);
        Assert.Equal("7CFF2FD251E3D14DC6C6F8534824A373", message.ChatId);
        Assert.Equal("A1B2C3D4E5F60718293A4B5C6D7E8F90", message.OpenId);
        Assert.Equal("今天吃火锅吗", message.Content);
        Assert.False(message.MentionsBot);
    }

    [Fact]
    public void FullGroupMessage_DetectsAtTagInContent()
    {
        const string payload = """
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":43,"d":{
              "id":"MSG-2","content":"<qqbot-at-user id=\"BOTID\" /> 在吗",
              "group_openid":"7CFF2FD251E3D14DC6C6F8534824A373",
              "author":{"member_openid":"A1B2C3D4E5F60718293A4B5C6D7E8F90"}}}
            """;

        var message = Map(payload);

        Assert.NotNull(message);
        Assert.True(message!.MentionsBot);
        Assert.Equal("在吗", message.Content);
    }

    [Fact]
    public void MapsDirectMessage()
    {
        const string payload = """
            {"op":0,"t":"C2C_MESSAGE_CREATE","s":44,"d":{
              "id":"MSG-3","content":"你好",
              "author":{"user_openid":"USER-OPEN-ID","union_openid":"UNION-1"}}}
            """;

        var message = Map(payload);

        Assert.NotNull(message);
        Assert.Equal(ChatKind.Direct, message!.Kind);
        Assert.Equal("USER-OPEN-ID", message.ChatId);
        Assert.Equal("UNION-1", message.UnionOpenId);
        Assert.True(message.MentionsBot);
    }

    [Fact]
    public void AtMessage_IsTreatedAsMention()
    {
        const string payload = """
            {"op":0,"t":"GROUP_AT_MESSAGE_CREATE","s":45,"d":{
              "id":"MSG-4","content":"在吗",
              "group_openid":"GRP",
              "author":{"member_openid":"MEMBER-1"}}}
            """;

        var message = Map(payload);
        Assert.NotNull(message);
        Assert.True(message!.MentionsBot);
    }

    [Fact]
    public void FullGroupMessage_DetectsBotMentionViaMentionsArray()
    {
        // 官方文档：全量模式下 content 里的 @ 会变成 <@member_openid>，被 @ 的人列在 mentions 里，
        // 且每个 mention 带 bot 布尔字段。这是判断"有没有 @ 我"的唯一可靠依据。
        const string payload = """
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":48,"d":{
              "id":"MSG-6",
              "content":"<@BOTOPENID> 在吗",
              "group_openid":"GRP",
              "author":{"id":"M1","member_openid":"M1","username":"小明","bot":false},
              "mentions":[{"id":"BOTOPENID","member_openid":"BOTOPENID","username":"小鲸鱼","bot":true}]}}
            """;

        var message = Map(payload);

        Assert.NotNull(message);
        Assert.True(message!.MentionsBot);
        Assert.Equal("@小鲸鱼 在吗", message.Content);
        Assert.Equal("小明", message.SenderName);
        Assert.Equal("GROUP_MESSAGE_CREATE", message.EventType);
    }

    [Fact]
    public void FullGroupMessage_MentioningSomeoneElse_IsNotBotMention()
    {
        const string payload = """
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":49,"d":{
              "id":"MSG-7",
              "content":"<@OTHER> 你看这个",
              "group_openid":"GRP",
              "author":{"member_openid":"M1","username":"小明"},
              "mentions":[{"member_openid":"OTHER","username":"小红","bot":false}]}}
            """;

        var message = Map(payload);

        Assert.NotNull(message);
        Assert.False(message!.MentionsBot);
        Assert.Equal("@小红 你看这个", message.Content);
    }

    [Fact]
    public void BotOwnMessage_IsMarkedAsSelf_ToAvoidLoops()
    {
        const string payload = """
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":51,"d":{
              "id":"MSG-9","content":"六点半吧",
              "group_openid":"GRP",
              "author":{"id":"BOT","member_openid":"BOT","username":"小鲸鱼","bot":true}}}
            """;

        var message = Map(payload);
        Assert.NotNull(message);
        Assert.True(message!.IsSelf);
    }

    [Fact]
    public void MediaOnlyMessage_IsDropped()
    {
        // 机器人看不到图片，也不该把"对方发了张图"编进对话。
        const string image = """
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":50,"d":{
              "id":"MSG-8","content":" ","group_openid":"GRP",
              "author":{"member_openid":"M1"},
              "attachments":[{"content_type":"image/jpeg","filename":"a.jpg","url":"https://x/y"}]}}
            """;

        Assert.Null(Map(image));

        var text = Map("""
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":52,"d":{
              "id":"MSG-10","content":"看这张图","group_openid":"GRP",
              "author":{"member_openid":"M1"},
              "attachments":[{"content_type":"image/jpeg","filename":"a.jpg","url":"https://x/y"}]}}
            """);

        // 带文字的消息照常进对话，但只保留文字部分。
        Assert.NotNull(text);
        Assert.Equal("看这张图", text!.Content);
    }

    [Fact]
    public void StripsFacePlaceholders()
    {
        const string payload = """
            {"op":0,"t":"GROUP_MESSAGE_CREATE","s":46,"d":{
              "id":"MSG-5","content":"笑死<faceType=1,faceId=\"314\",ext=\"x\">",
              "group_openid":"GRP",
              "author":{"member_openid":"MEMBER-1"}}}
            """;

        var message = Map(payload);
        Assert.NotNull(message);
        Assert.Equal("笑死[表情]", message!.Content);
    }

    [Fact]
    public void UnknownShape_ReturnsNull()
    {
        var message = Map("""{"op":0,"t":"GROUP_MESSAGE_CREATE","s":47,"d":{"id":"X","content":"y"}}""");
        Assert.Null(message);
    }

    [Fact]
    public void OutboundGroupText_DoesNotEmbedUnsupportedMentionTag()
    {
        var message = new OutgoingMessage("GRP", ChatKind.Group, "名字已改为「吴彦祖」", "086FFB033C2B7F5277D50D22C127416C");

        var content = QqChatAdapter.BuildContent(message);

        Assert.Equal("名字已改为「吴彦祖」", content);
        Assert.DoesNotContain("qqbot-at-user", content);
    }

    private static IncomingMessage? Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        var type = document.RootElement.GetProperty("t").GetString();
        return QqPayloadMapper.Map(document.RootElement, type);
    }
}
