using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;

namespace TransferProof.Core.Matching;

public static partial class AmountParser
{
    public static bool TryParse(string raw, out decimal amount, out string? currency)
    {
        amount = 0;
        currency = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Normalize(NormalizationForm.FormKC).Trim();
        if (value.Contains('₩') || value.Contains('원') || value.Contains("KRW", StringComparison.OrdinalIgnoreCase)) currency = "KRW";
        else if (value.Contains('$') || value.Contains("USD", StringComparison.OrdinalIgnoreCase)) currency = "USD";
        value = CurrencyRegex().Replace(value, "").Trim();
        if (Regex.IsMatch(value, @"^\d{1,3}(\.\d{3}){2,}$")) value = value.Replace(".", "");
        else if (Regex.IsMatch(value, @"^\d+\.\d{3}$")) return false;
        if (value.Contains(',') && !Regex.IsMatch(value, @"^\d{1,3}(,\d{3})+(\.\d{1,2})?$")) return false;
        if (value.Contains(' ') && !Regex.IsMatch(value, @"^\d{1,3}( \d{3})+(\.\d{1,2})?$")) return false;
        value = value.Replace(",", "").Replace(" ", "");
        return Regex.IsMatch(value, @"^\d+(\.\d{1,2})?$") &&
            decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }
    [GeneratedRegex(@"KRW|USD|₩|원|\$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyRegex();
}

public static class NameNormalizer
{
    public static string Normalize(string? value) => new((value ?? "").Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
public static class NameMatcher
{
    public static double Similarity(string? a, string? b)
    {
        a = NameNormalizer.Normalize(a); b = NameNormalizer.Normalize(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var previous = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return 1d - (double)row[b.Length] / Math.Max(a.Length, b.Length);
    }
}
public static class AccountMatcher
{
    public static bool Matches(string? a, string? b)
    {
        // Masked positions remain unknown; only a fully visible suffix can establish a match.
        static string Suffix(string? value)
        {
            var normalized = (value ?? "").Normalize(NormalizationForm.FormKC);
            var significant = new string(normalized.Where(c => char.IsAsciiDigit(c) || c is '*' or 'X' or 'x' or '•').ToArray());
            return significant.Length >= 4 ? significant[^4..] : significant;
        }
        var left = Suffix(a); var right = Suffix(b);
        return left.Length == 4 && right.Length == 4 && left.All(char.IsAsciiDigit) && right.All(char.IsAsciiDigit) && left == right;
    }
    public static string Mask(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? "***" + digits[^4..] : "***";
    }
}
public sealed class MatchingOptions
{
    [System.ComponentModel.DataAnnotations.Range(0, 1)] public double ReceiverNameThreshold { get; set; } = 0.8;
    [System.ComponentModel.DataAnnotations.Range(0, 1)] public double SenderNameThreshold { get; set; } = 0.7;
    [System.ComponentModel.DataAnnotations.Range(0, 1)] public double MinConfidence { get; set; } = 0.6;
}
public static class ProofMatcher
{
    public static MatchResult Match(PaymentOrder order, TransferExtraction? best, MatchingOptions options, DateTimeOffset now)
    {
        var checks = new List<FieldCheck>();
        void Check(string field, object? expected, object? actual, bool passed, string? reason = null) =>
            checks.Add(new(field, Convert.ToString(expected, CultureInfo.InvariantCulture), Convert.ToString(actual, CultureInfo.InvariantCulture), passed, reason ?? (passed ? "matched" : "mismatch")));
        Check("amount", order.Amount, best?.Amount, best?.Amount == order.Amount, best?.Amount is null ? "missing" : null);
        Check("receiverAccount", AccountMatcher.Mask(order.SellerAccount), AccountMatcher.Mask(best?.ReceiverAccount), AccountMatcher.Matches(order.SellerAccount, best?.ReceiverAccount), string.IsNullOrWhiteSpace(best?.ReceiverAccount) ? "missing" : null);
        Check("receiverName", order.SellerName, best?.ReceiverName, NameMatcher.Similarity(order.SellerName, best?.ReceiverName) >= options.ReceiverNameThreshold);
        Check("senderName", order.BuyerName, best?.SenderName, NameMatcher.Similarity(order.BuyerName, best?.SenderName) >= options.SenderNameThreshold, string.IsNullOrWhiteSpace(best?.SenderName) ? "missing" : null);
        Check("transferredAt", $"{order.CreatedAt.AddMinutes(-10):O}..{now.AddMinutes(2):O}", best?.TransferredAt,
            best?.TransferredAt is null || best.TransferredAt >= order.CreatedAt.AddMinutes(-10) && best.TransferredAt <= now.AddMinutes(2), best?.TransferredAt is null ? "skipped" : null);
        Check("isCompletionScreen", true, best?.IsCompletionScreen, best?.IsCompletionScreen == true);
        Check("confidence", options.MinConfidence, best?.Confidence, best?.Confidence >= options.MinConfidence && best.Confidence <= 1);
        Check("currency", order.Currency, best?.Currency, string.Equals(order.Currency, best?.Currency, StringComparison.OrdinalIgnoreCase));
        var unreadable = best?.Amount is null || string.IsNullOrWhiteSpace(best.ReceiverAccount);
        return new(unreadable ? Verdict.Unreadable : checks.All(c => c.Passed) ? Verdict.Matched : Verdict.Mismatched, checks);
    }
}
public static class FrameSelector
{
    public static IReadOnlyList<FrameScore> Select(IReadOnlyList<FrameScore> frames, int max) => frames
        .Where(f => !f.IsLikelyLoadingScreen).OrderByDescending(f => f.Score).ThenByDescending(f => f.Index).Take(max).ToArray();
}
