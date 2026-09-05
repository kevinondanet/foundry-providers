namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// The dependency-free distribution functions of <c>scorer/_metrics/std.py</c> (<c>_t_inv_cdf</c>,
/// <c>_reg_inc_beta</c>, <c>_beta_continued_fraction</c>) and CPython's <c>NormalDist.inv_cdf</c>
/// (Wichura's AS241), ported step for step so the critical values match Python to floating-point precision.
/// </summary>
public static class Distributions
{
    /// <summary>
    /// Port of <c>_t_inv_cdf</c>: the Student-t quantile, exact to bisection precision via the regularized
    /// incomplete beta function (for t ≥ 0, <c>F(t) = 1 - I_x(df/2, 1/2) / 2</c> with <c>x = df / (df + t²)</c>).
    /// </summary>
    public static double TInvCdf(double p, int df)
    {
        if (!(p > 0.0 && p < 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, $"t quantile requires 0 < p < 1, got {p}");
        }

        if (df < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(df), df, $"t quantile requires df >= 1, got {df}");
        }

        if (p == 0.5)
        {
            return 0.0;
        }

        if (p < 0.5)
        {
            return -TInvCdf(1.0 - p, df);
        }

        double Cdf(double t)
        {
            var x = df / (df + t * t);
            return 1.0 - 0.5 * RegularizedIncompleteBeta(df / 2.0, 0.5, x);
        }

        var hi = 1.0;
        while (Cdf(hi) < p)
        {
            hi *= 2.0;
        }

        var lo = 0.0;
        for (var i = 0; i < 100; i++)
        {
            var mid = (lo + hi) / 2.0;
            if (Cdf(mid) < p)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2.0;
    }

    /// <summary>Port of <c>_reg_inc_beta</c>: the regularized incomplete beta function I_x(a, b) (Numerical Recipes 6.4).</summary>
    public static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0.0)
        {
            return 0.0;
        }

        if (x >= 1.0)
        {
            return 1.0;
        }

        var lnFront = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1.0 - x);
        var front = Math.Exp(lnFront);
        return x < (a + 1.0) / (a + b + 2.0)
            ? front * BetaContinuedFraction(a, b, x) / a
            : 1.0 - front * BetaContinuedFraction(b, a, 1.0 - x) / b;
    }

    /// <summary>Port of <c>_beta_continued_fraction</c>: Lentz's continued fraction for the incomplete beta function.</summary>
    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 200;
        const double epsilon = 3e-16;
        const double tiny = 1e-300;

        var qab = a + b;
        var qap = a + 1.0;
        var qam = a - 1.0;
        var c = 1.0;
        var d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < tiny)
        {
            d = tiny;
        }

        d = 1.0 / d;
        var h = d;
        for (var m = 1; m <= maxIterations; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            c = 1.0 + aa / c;
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            c = 1.0 + aa / c;
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            d = 1.0 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1.0) < epsilon)
            {
                break;
            }
        }

        return h;
    }

    /// <summary>Python <c>math.lgamma</c>: Lanczos approximation (g = 7, n = 9), accurate to ~1e-15 for positive arguments.</summary>
    public static double LogGamma(double x)
    {
        if (x <= 0 && x == Math.Floor(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "lgamma is undefined at non-positive integers.");
        }

        if (x < 0.5)
        {
            // reflection: Γ(x)Γ(1 - x) = π / sin(πx)
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1.0 - x);
        }

        x -= 1.0;
        var a = LanczosCoefficients[0];
        var t = x + 7.5;
        for (var i = 1; i < LanczosCoefficients.Length; i++)
        {
            a += LanczosCoefficients[i] / (x + i);
        }

        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    private static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993,
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    /// <summary>Port of CPython's <c>NormalDist().inv_cdf(p)</c> (Wichura, Algorithm AS241) for the standard normal.</summary>
    public static double NormalInvCdf(double p)
    {
        if (!(p > 0.0 && p < 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p must be in the range 0.0 < p < 1.0");
        }

        var q = p - 0.5;
        double num;
        double den;
        double r;
        if (Math.Abs(q) <= 0.425)
        {
            r = 0.180625 - q * q;
            num = (((((((2.5090809287301226727e+3 * r
                + 3.3430575583588128105e+4) * r
                + 6.7265770927008700853e+4) * r
                + 4.5921953931549871457e+4) * r
                + 1.3731693765509461125e+4) * r
                + 1.9715909503065514427e+3) * r
                + 1.3314166789178437745e+2) * r
                + 3.3871328727963666080e+0) * q;
            den = (((((((5.2264952788528545610e+3 * r
                + 2.8729085735721942674e+4) * r
                + 3.9307895800092710610e+4) * r
                + 2.1213794301586595867e+4) * r
                + 5.3941960214247511077e+3) * r
                + 6.8718700749205790830e+2) * r
                + 4.2313330701600911252e+1) * r
                + 1.0);
            return num / den;
        }

        r = q <= 0.0 ? p : 1.0 - p;
        r = Math.Sqrt(-Math.Log(r));
        if (r <= 5.0)
        {
            r -= 1.6;
            num = (((((((7.7454501427834140764e-4 * r
                + 2.27238449892691845833e-2) * r
                + 2.41780725177450611770e-1) * r
                + 1.27045825245236838258e+0) * r
                + 3.64784832476320460504e+0) * r
                + 5.76949722146069140550e+0) * r
                + 4.63033784615654529590e+0) * r
                + 1.42343711074968357734e+0);
            den = (((((((1.05075007164441684324e-9 * r
                + 5.47593808499534494600e-4) * r
                + 1.51986665636164571966e-2) * r
                + 1.48103976427480074590e-1) * r
                + 6.89767334985100004550e-1) * r
                + 1.67638483018380384940e+0) * r
                + 2.05319162663775882187e+0) * r
                + 1.0);
        }
        else
        {
            r -= 5.0;
            num = (((((((2.01033439929228813265e-7 * r
                + 2.71155556874348757815e-5) * r
                + 1.24266094738807843860e-3) * r
                + 2.65321895265761230930e-2) * r
                + 2.96560571828504891230e-1) * r
                + 1.78482653991729133580e+0) * r
                + 5.46378491116411436990e+0) * r
                + 6.65790464350110377720e+0);
            den = (((((((2.04426310338993978564e-15 * r
                + 1.42151175831644588870e-7) * r
                + 1.84631831751005468180e-5) * r
                + 7.86869131145613259100e-4) * r
                + 1.48753612908506148525e-2) * r
                + 1.36929880922735805310e-1) * r
                + 5.99832206555887937690e-1) * r
                + 1.0);
        }

        var x = num / den;
        return q < 0.0 ? -x : x;
    }
}
