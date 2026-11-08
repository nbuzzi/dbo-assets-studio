using System.Text.RegularExpressions;

namespace DboAssetsStudio;

// Separate EFF v7 contract. Legacy v3..v6 retain their strict identifier rules.
// These two native Source names contain a literal hyphen, without a path or
// command interpretation. Their exact serialized records must be sealed.
internal static partial class EffectMergeService
{
    private static void ValidateLiteralSplinePolicyShape(AdvancedPlan plan)
    {
        if (plan.Version != 7)
        {
            if (plan.LiteralSplineNames is not null)
                throw new InvalidDataException("Literal spline policy is exclusive to EFF v7.");
            return;
        }
        if (plan.LiteralSplineNames is not { Count: > 0 and <= 2 } ||
            plan.LiteralSplineNames.Any(p => p is null || p.Name is not
                ("Hot-tempered_Rush_MGR_A024" or "Hot-tempered_Rush_MGR_A024_S") ||
                p.RecordSha256 is not { Length:64 } || !p.RecordSha256.All(Uri.IsHexDigit)) ||
            plan.LiteralSplineNames.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != plan.LiteralSplineNames.Count ||
            !plan.LiteralSplineNames.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(
                plan.Splines.Where(name => !Regex.IsMatch(name,"^[A-Za-z0-9_]{1,128}$"))))
            throw new InvalidDataException("EFF v7 requires exact referenced literal names and record SHA-256 seals.");
    }
    private static void ValidateLiteralSplinePolicyRecords(AdvancedPlan plan, Catalog source)
    {
        foreach (var policy in plan.LiteralSplineNames ?? new List<LiteralSplinePolicy>())
        {
            var rows=source.Splines.Where(r => r.Name == policy.Name).ToArray();
            if (rows.Length != 1 || !Equal(Hash(source.Raw.AsSpan(rows[0].Offset,rows[0].Length).ToArray()),policy.RecordSha256))
                throw new InvalidDataException("Literal spline record differs from sealed native source.");
        }
    }
}
