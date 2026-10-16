using DSQQ.Models;

namespace DSQQ.Tests.Models;

// 这两个字段的切分规则必须**不一样**，而且理由在服务端而不是随手定的：
// keywords 按逗号切（空格是内容），speakerIds 按逗号或空白切。
//
// 这条测试是从一个真实缺陷补回来的：两个都用「逗号或空白」切时，
// 桥接默认关键词里的「DeepSeek V3」每保存一次就被拆成两个词。
public class WakeListParserTests
{
    [Fact]
    public void 关键词里的空格是内容不是分隔符()
    {
        var result = WakeListParser.ParseKeywords("DeepSeek V3, DeepSeek R1, R1");

        Assert.Equal(["DeepSeek V3", "DeepSeek R1", "R1"], result);
    }

    [Fact]
    public void 关键词也认中文逗号和换行()
    {
        var result = WakeListParser.ParseKeywords("小鲸鱼，大肥鱼\r\n鲸鱼");

        Assert.Equal(["小鲸鱼", "大肥鱼", "鲸鱼"], result);
    }

    [Fact]
    public void 指定成员按空白切因为QQ号之间常用空格分隔()
    {
        var result = WakeListParser.ParseSpeakerIds("1416935939 3551510664, 415520021");

        Assert.Equal(["1416935939", "3551510664", "415520021"], result);
    }

    [Fact]
    public void 指定成员里的多个连续空格不产生空项()
    {
        var result = WakeListParser.ParseSpeakerIds("111   222\t\t333");

        Assert.Equal(["111", "222", "333"], result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",，,")]
    public void 空输入得到空列表而不是空字符串项(string? text)
    {
        Assert.Empty(WakeListParser.ParseKeywords(text));
        Assert.Empty(WakeListParser.ParseSpeakerIds(text));
    }

    [Fact]
    public void 两端的空白被去掉但中间的保留()
    {
        var result = WakeListParser.ParseKeywords("  DeepSeek V3  ,  R1  ");

        Assert.Equal(["DeepSeek V3", "R1"], result);
    }

    [Fact]
    public void 关键词切分不会把空格拆成两个词这正是当初的缺陷()
    {
        // 缺陷版本：按 [,，\s] 切 → ["DeepSeek", "V3"]，每保存一次再碎一层。
        var parsed = WakeListParser.ParseKeywords("DeepSeek V3");

        Assert.Single(parsed);
        Assert.Equal("DeepSeek V3", parsed[0]);
    }
}
