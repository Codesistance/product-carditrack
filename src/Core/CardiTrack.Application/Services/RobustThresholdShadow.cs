namespace CardiTrack.Application.Services;

/// <summary>
/// Shadow comparison of the shipped 30%-of-mean steps/sleep fences against a median ± k·MAD
/// fence. Does not fire alerts — <c>StatisticalAlertRules</c> is unchanged. Used to evaluate
/// G2 (<c>docs/technical/mathnet_numerics.md</c>) on stored readings without switching live
/// rules. MAD is unscaled (median of |x − median|), matching <c>IDescriptiveStatistics</c>.
/// </summary>
public static class RobustThresholdShadow
{
    /// <summary>Same fraction the live activity/sleep rules use.</summary>
    public const double MeanDeviationFraction = StatisticalAlertRules.DeviationFraction;

    /// <summary>
    /// Conventional 3× unscaled-MAD fence. Not a product threshold — live alerts still use
    /// 30% of the mean. When MAD is zero the fence is undefined and does not fire.
    /// </summary>
    public const double MadFenceK = 3.0;

    public readonly record struct Verdict(bool MeanRuleFires, bool MadFenceFires)
    {
        public bool Agree => MeanRuleFires == MadFenceFires;
        public bool MeanOnly => MeanRuleFires && !MadFenceFires;
        public bool MadOnly => !MeanRuleFires && MadFenceFires;
    }

    /// <summary>Activity decline: a one-sided drop below the yardstick.</summary>
    public static Verdict CompareDecline(double value, double mean, double median, double mad)
    {
        var meanFires = mean > 0 && value < mean * (1 - MeanDeviationFraction);
        var madFires = mad > 0 && value < median - (MadFenceK * mad);
        return new Verdict(meanFires, madFires);
    }

    /// <summary>Irregular sleep: a two-sided departure from the yardstick.</summary>
    public static Verdict CompareTwoSided(double value, double mean, double median, double mad)
    {
        var meanFires = mean > 0 && Math.Abs(value - mean) > mean * MeanDeviationFraction;
        var madFires = mad > 0 && Math.Abs(value - median) > MadFenceK * mad;
        return new Verdict(meanFires, madFires);
    }
}
