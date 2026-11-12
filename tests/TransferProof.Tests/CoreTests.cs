using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;
using TransferProof.Core.Matching;

namespace TransferProof.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("150,000", 150000, null)]
    [InlineData("₩150,000", 150000, "KRW")]
    [InlineData("KRW 150,000", 150000, "KRW")]
    [InlineData("150,000원", 150000, "KRW")]
    [InlineData("$1,234.56", 1234.56, "USD")]
    [InlineData("1.234.567", 1234567, null)]
    [InlineData("1 234 567", 1234567, null)]
    [InlineData("１５０，０００", 150000, null)]
    [InlineData("0", 0, null)]
    [InlineData("1234.50", 1234.5, null)]
    public void TryParse_RecognizedAmount_ReturnsValue(string raw, double expected, string? currency)
    {
        Assert.True(AmountParser.TryParse(raw, out var amount, out var actualCurrency)); Assert.Equal((decimal)expected, amount); Assert.Equal(currency, actualCurrency);
    }
    [Theory]
    [InlineData("1.234")][InlineData("12,34")][InlineData("abc")][InlineData("1,2,3")][InlineData("")][InlineData("-1")][InlineData("1 23")]
    public void TryParse_AmbiguousOrInvalid_ReturnsFalse(string raw) => Assert.False(AmountParser.TryParse(raw, out _, out _));
    [Theory]
    [InlineData("Alex Chen", "Ａｌｅｘ-Ｃｈｅｎ", 1)]
    [InlineData("ABCD", "ABCE", 0.75)]
    [InlineData("", "Alex", 0)]
    public void Similarity_NormalizedNames_ReturnsExpected(string a, string b, double expected) => Assert.Equal(expected, NameMatcher.Similarity(a, b), 5);
    [Theory]
    [InlineData("110-234-567890", "110-***-567890", true)]
    [InlineData("1234", "1234", true)]
    [InlineData("1234", "12**", false)]
    [InlineData("1234", "123", false)]
    [InlineData("1234", "9234", false)]
    public void Matches_MaskedAccounts_RequiresVisibleSuffix(string a, string b, bool expected) => Assert.Equal(expected, AccountMatcher.Matches(a, b));
    [Theory]
    [InlineData("matched", Verdict.Matched)]
    [InlineData("amount", Verdict.Mismatched)]
    [InlineData("account", Verdict.Mismatched)]
    [InlineData("missing", Verdict.Unreadable)]
    [InlineData("screen", Verdict.Mismatched)]
    [InlineData("time", Verdict.Mismatched)]
    [InlineData("confidence", Verdict.Mismatched)]
    [InlineData("sender", Verdict.Mismatched)]
    [InlineData("currency", Verdict.Mismatched)]
    public void Match_FieldScenarios_ReturnsVerdict(string scenario, Verdict expected)
    {
        var now = new ManualTimeProvider().GetUtcNow(); var input = TestMedia.Extraction(now);
        input = scenario switch {
            "amount" => input with { Amount = 149999 }, "account" => input with { ReceiverAccount = "1111" },
            "missing" => input with { Amount = null }, "screen" => input with { IsCompletionScreen = false },
            "time" => input with { TransferredAt = now.AddMinutes(-11) }, "confidence" => input with { Confidence = 0.5 },
            "sender" => input with { SenderName = null }, "currency" => input with { Currency = "USD" }, _ => input };
        var result = ProofMatcher.Match(TestMedia.Order(now), input, new(), now);
        Assert.Equal(expected, result.Verdict); Assert.Equal(8, result.Checks.Count);
        Assert.DoesNotContain(result.Checks, c => c.Actual?.Contains("567890", StringComparison.Ordinal) == true);
    }
    [Fact]
    public void Match_MissingTime_SkipsTimeCheck()
    {
        var now = new ManualTimeProvider().GetUtcNow();
        var result = ProofMatcher.Match(TestMedia.Order(now), TestMedia.Extraction(now) with { TransferredAt = null }, new(), now);
        Assert.Equal(Verdict.Matched, result.Verdict); Assert.Equal("skipped", Assert.Single(result.Checks, c => c.Field == "transferredAt").Reason);
    }
    [Fact]
    public void Select_ScoreTies_PrefersLaterAndExcludesLoading()
    {
        FrameScore[] frames = [new("a", 1, 500, 0.1, false, 1), new("b", 2, 500, 0.1, false, 1), new("c", 3, 0, 0, true, 5)];
        Assert.Equal("b", Assert.Single(FrameSelector.Select(frames, 1)).Path);
        Assert.Empty(FrameSelector.Select([frames[2]], 3));
    }
}
