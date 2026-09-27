namespace WikipediaInterestSkill.TimeSeries;

internal static class Loess
{
    public static bool Estimate(
        ReadOnlySpan<double> y,
        int n,
        int len,
        int degree,
        double xs,
        int nleft,
        int nright,
        Span<double> w,
        bool useWeights,
        ReadOnlySpan<double> rw,
        out double ys)
    {
        var range = n - 1.0;
        var h = Math.Max(xs - nleft, nright - xs);
        if (len > n) h += (len - n) / 2;
        var h9 = 0.999 * h;
        var h1 = 0.001 * h;

        var a = 0.0;
        for (var j = nleft; j <= nright; j++)
        {
            w[j - 1] = 0.0;
            var r = Math.Abs(j - xs);
            if (r > h9) continue;
            if (r <= h1)
            {
                w[j - 1] = 1.0;
            }
            else
            {
                var q = r / h;
                var t = 1.0 - q * q * q;
                w[j - 1] = t * t * t;
            }

            if (useWeights) w[j - 1] *= rw[j - 1];
            a += w[j - 1];
        }

        if (a <= 0.0)
        {
            ys = 0.0;
            return false;
        }

        for (var j = nleft; j <= nright; j++) w[j - 1] /= a;

        if (h > 0.0 && degree > 0)
        {
            a = 0.0;
            for (var j = nleft; j <= nright; j++) a += w[j - 1] * j;
            var b = xs - a;
            var c = 0.0;
            for (var j = nleft; j <= nright; j++) c += w[j - 1] * (j - a) * (j - a);
            if (Math.Sqrt(c) > 0.001 * range)
            {
                b /= c;
                for (var j = nleft; j <= nright; j++) w[j - 1] *= b * (j - a) + 1.0;
            }
        }

        var sum = 0.0;
        for (var j = nleft; j <= nright; j++) sum += w[j - 1] * y[j - 1];
        ys = sum;
        return true;
    }

    public static void Smooth(
        ReadOnlySpan<double> y,
        int n,
        int len,
        int degree,
        bool useWeights,
        ReadOnlySpan<double> rw,
        Span<double> ys,
        Span<double> work)
    {
        if (n < 2)
        {
            ys[0] = y[0];
            return;
        }

        if (len >= n)
        {
            for (var i = 1; i <= n; i++)
            {
                if (!Estimate(y, n, len, degree, i, 1, n, work, useWeights, rw, out var v)) v = y[i - 1];
                ys[i - 1] = v;
            }

            return;
        }

        var nsh = (len + 1) / 2;
        var nleft = 1;
        var nright = len;
        for (var i = 1; i <= n; i++)
        {
            if (i > nsh && nright != n)
            {
                nleft++;
                nright++;
            }

            if (!Estimate(y, n, len, degree, i, nleft, nright, work, useWeights, rw, out var v)) v = y[i - 1];
            ys[i - 1] = v;
        }
    }

    public static void RobustnessWeights(
        ReadOnlySpan<double> y, ReadOnlySpan<double> fit, Span<double> rw, Span<double> r, Span<double> sorted)
    {
        var n = y.Length;
        for (var i = 0; i < n; i++) r[i] = Math.Abs(y[i] - fit[i]);
        r[..n].CopyTo(sorted);
        sorted[..n].Sort();
        var mid1 = n / 2;
        var mid2 = n - mid1 - 1;
        var cmad = 3.0 * (sorted[mid1] + sorted[mid2]);
        var c9 = 0.999 * cmad;
        var c1 = 0.001 * cmad;
        for (var i = 0; i < n; i++)
        {
            if (r[i] <= c1)
            {
                rw[i] = 1.0;
            }
            else if (r[i] <= c9)
            {
                var u = r[i] / cmad;
                var t = 1.0 - u * u;
                rw[i] = t * t;
            }
            else
            {
                rw[i] = 0.0;
            }
        }
    }
}

internal sealed class LoessPlan
{
    private readonly int _n;
    private readonly int _degree;
    private readonly int[] _left;
    private readonly int[] _right;
    private readonly double[] _xs;
    private readonly double[] _h;
    private readonly double[][] _kernel;
    private readonly double[]?[] _unweighted;
    private readonly double[] _work;

    public LoessPlan(int n, int len, int degree)
    {
        _n = n;
        _degree = degree;
        _left = new int[n];
        _right = new int[n];
        _xs = new double[n];
        _h = new double[n];
        _kernel = new double[n][];
        _unweighted = new double[n][];
        var nsh = (len + 1) / 2;
        var nleft = 1;
        var nright = len >= n ? n : len;
        for (var i = 1; i <= n; i++)
        {
            if (len < n && i > nsh && nright != n)
            {
                nleft++;
                nright++;
            }

            double h = Math.Max(i - nleft, nright - i);
            if (len > n) h += (len - n) / 2;
            var kernel = new double[nright - nleft + 1];
            var h9 = 0.999 * h;
            var h1 = 0.001 * h;
            for (var j = nleft; j <= nright; j++)
            {
                var r = Math.Abs(j - (double)i);
                if (r > h9) continue;
                if (r <= h1)
                {
                    kernel[j - nleft] = 1.0;
                }
                else
                {
                    var q = r / h;
                    var t = 1.0 - q * q * q;
                    kernel[j - nleft] = t * t * t;
                }
            }

            _left[i - 1] = nleft;
            _right[i - 1] = nright;
            _xs[i - 1] = i;
            _h[i - 1] = h;
            _kernel[i - 1] = kernel;
            var w = (double[])kernel.Clone();
            _unweighted[i - 1] = Finish(w, w.Length, nleft, h, i) ? w : null;
        }

        _work = new double[Math.Min(len, n) + 1];
    }

    public void Smooth(ReadOnlySpan<double> y, bool useWeights, ReadOnlySpan<double> rw, Span<double> ys)
    {
        for (var i = 0; i < _n; i++)
        {
            var nleft = _left[i];
            var nright = _right[i];
            double[]? w;
            if (useWeights)
            {
                w = _work;
                var k = _kernel[i];
                for (var j = nleft; j <= nright; j++) w[j - nleft] = k[j - nleft] * rw[j - 1];
                if (!Finish(w, nright - nleft + 1, nleft, _h[i], _xs[i])) w = null;
            }
            else
            {
                w = _unweighted[i];
            }

            if (w is null)
            {
                ys[i] = y[i];
                continue;
            }

            var sum = 0.0;
            for (var j = nleft; j <= nright; j++) sum += w[j - nleft] * y[j - 1];
            ys[i] = sum;
        }
    }

    private bool Finish(double[] w, int count, int nleft, double h, double xs)
    {
        var a = 0.0;
        for (var j = 0; j < count; j++) a += w[j];
        if (a <= 0.0) return false;
        for (var j = 0; j < count; j++) w[j] /= a;
        if (h > 0.0 && _degree > 0)
        {
            a = 0.0;
            for (var j = 0; j < count; j++) a += w[j] * (nleft + j);
            var b = xs - a;
            var c = 0.0;
            for (var j = 0; j < count; j++) c += w[j] * (nleft + j - a) * (nleft + j - a);
            if (Math.Sqrt(c) > 0.001 * (_n - 1.0))
            {
                b /= c;
                for (var j = 0; j < count; j++) w[j] *= b * (nleft + j - a) + 1.0;
            }
        }

        return true;
    }
}
