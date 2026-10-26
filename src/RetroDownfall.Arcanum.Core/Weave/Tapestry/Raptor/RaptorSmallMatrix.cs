using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>Original managed implementation of standard Cholesky and triangular-solve Gaussian equations.</summary>
internal static class RaptorSmallMatrix
{
    internal static Result<double[][]> Cholesky(double[][] covariance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (covariance is null) return Invalid();

        int dimensions = covariance.Length;

        var options = RaptorAlgorithmManifest.Current.Options;

        if (dimensions < 1 || dimensions > options.ReductionDimensions || covariance.Any(row => row is null || row.Length != dimensions)) return Invalid();

        double[][] lower = new double[dimensions][];

        for (int row = 0; row < dimensions; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lower[row] = new double[dimensions];

            for (int column = 0; column <= row; column++)
            {
                double value = covariance[row][column];

                double mirror = covariance[column][row];

                if (!double.IsFinite(value) || !double.IsFinite(mirror) || Math.Abs(value - mirror) > options.MatrixSymmetryTolerance * Math.Max(1, Math.Max(Math.Abs(value), Math.Abs(mirror)))) return Invalid();

                for (int index = 0; index < column; index++) value -= lower[row][index] * lower[column][index];

                if (!double.IsFinite(value) || (row == column && value <= 0)) return Invalid();

                lower[row][column] = row == column ? Math.Sqrt(value) : value / lower[column][column];

                if (!double.IsFinite(lower[row][column])) return Invalid();
            }
        }

        return lower;
    }

    internal static double LogDeterminant(double[][] lower)
    {
        double result = 0;

        for (int index = 0; index < lower.Length; index++) result += 2 * Math.Log(lower[index][index]);

        return result;
    }

    internal static double LogDensity(ReadOnlySpan<double> point, ReadOnlySpan<double> mean, double[][] lower, CancellationToken cancellationToken)
    {
        Span<double> solved = stackalloc double[10];

        double distance = 0;

        for (int row = 0; row < lower.Length; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double value = point[row] - mean[row];

            for (int column = 0; column < row; column++) value -= lower[row][column] * solved[column];

            solved[row] = value / lower[row][row];

            distance += solved[row] * solved[row];
        }

        return -0.5 * (lower.Length * Math.Log(2 * Math.PI) + LogDeterminant(lower) + distance);
    }

    private static Error Invalid() => new("Raptor.SingularCovariance", "Covariance is invalid or not positive definite.");
}
