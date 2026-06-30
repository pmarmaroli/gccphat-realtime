using System;

namespace GccPhat.Core;

public readonly record struct DifferentialBeamformerDesign(
    int Order,
    int CandidateOrder,
    int DistinctProjectedPositions,
    double ApertureMeters,
    double StabilityRatio)
{
    public bool IsUsable => Order > 0;
}

/// <summary>
/// Designs auto-order differential beam weights from an arbitrary 2D microphone geometry.
/// The microphones are projected onto the steering axis, and the highest stable differential
/// order supported by the projected geometry is selected automatically.
/// </summary>
public static class DifferentialBeamformerDesigner
{
    private const double DistinctProjectionTolerance = 1e-3;
    private const double StabilityThreshold = 1e-6;
    private const double ConstraintTolerance = 1e-6;

    public static bool TryBuildWeights(double[] micX, double[] micY, double azimuthDeg, double[] weights, out DifferentialBeamformerDesign design)
    {
        if (micX is null) throw new ArgumentNullException(nameof(micX));
        if (micY is null) throw new ArgumentNullException(nameof(micY));
        if (weights is null) throw new ArgumentNullException(nameof(weights));
        if (micX.Length != micY.Length) throw new ArgumentException("micX and micY must have the same length.");
        if (micX.Length != weights.Length) throw new ArgumentException("weights length must match the geometry length.");

        Array.Clear(weights, 0, weights.Length);
        if (weights.Length < 2)
        {
            design = default;
            return false;
        }

        var projected = new double[weights.Length];
        double radians = azimuthDeg * Math.PI / 180.0;
        double ux = Math.Cos(radians);
        double uy = Math.Sin(radians);
        double mean = 0.0;
        for (int i = 0; i < weights.Length; i++)
        {
            projected[i] = micX[i] * ux + micY[i] * uy;
            mean += projected[i];
        }

        mean /= weights.Length;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double maxAbs = 0.0;
        for (int i = 0; i < projected.Length; i++)
        {
            projected[i] -= mean;
            min = Math.Min(min, projected[i]);
            max = Math.Max(max, projected[i]);
            maxAbs = Math.Max(maxAbs, Math.Abs(projected[i]));
        }

        double aperture = max - min;
        int distinctProjectedPositions = CountDistinctProjectedPositions(projected, DistinctProjectionTolerance);
        int candidateOrder = Math.Max(0, distinctProjectedPositions - 1);
        if (candidateOrder == 0 || maxAbs <= 1e-9)
        {
            design = new DifferentialBeamformerDesign(0, candidateOrder, distinctProjectedPositions, aperture, 0.0);
            return false;
        }

        for (int i = 0; i < projected.Length; i++)
        {
            projected[i] /= maxAbs;
        }

        var rawWeights = new double[weights.Length];
        for (int order = candidateOrder; order >= 1; order--)
        {
            if (!TrySolveWeightsForOrder(projected, order, rawWeights, out double stabilityRatio))
            {
                continue;
            }

            NormalizeWeights(rawWeights, weights);
            design = new DifferentialBeamformerDesign(order, candidateOrder, distinctProjectedPositions, aperture, stabilityRatio);
            return true;
        }

        design = new DifferentialBeamformerDesign(0, candidateOrder, distinctProjectedPositions, aperture, 0.0);
        Array.Clear(weights, 0, weights.Length);
        return false;
    }

    private static bool TrySolveWeightsForOrder(double[] projected, int order, double[] weights, out double stabilityRatio)
    {
        int constraintCount = order + 1;
        var gram = new double[constraintCount, constraintCount];
        var target = new double[constraintCount];
        target[order] = 1.0;

        for (int row = 0; row < constraintCount; row++)
        {
            for (int col = 0; col < constraintCount; col++)
            {
                double sum = 0.0;
                for (int i = 0; i < projected.Length; i++)
                {
                    sum += Math.Pow(projected[i], row + col);
                }
                gram[row, col] = sum;
            }
        }

        if (!TrySolveLinearSystem(gram, target, out double[] lagrange, out stabilityRatio) || stabilityRatio < StabilityThreshold)
        {
            Array.Clear(weights, 0, weights.Length);
            return false;
        }

        for (int i = 0; i < projected.Length; i++)
        {
            double weight = 0.0;
            double power = 1.0;
            for (int row = 0; row < constraintCount; row++)
            {
                weight += lagrange[row] * power;
                power *= projected[i];
            }
            weights[i] = weight;
        }

        for (int row = 0; row < order; row++)
        {
            double sum = 0.0;
            for (int i = 0; i < projected.Length; i++)
            {
                sum += weights[i] * Math.Pow(projected[i], row);
            }

            if (Math.Abs(sum) > ConstraintTolerance)
            {
                Array.Clear(weights, 0, weights.Length);
                return false;
            }
        }

        double response = 0.0;
        for (int i = 0; i < projected.Length; i++)
        {
            response += weights[i] * Math.Pow(projected[i], order);
        }

        if (Math.Abs(response) <= ConstraintTolerance)
        {
            Array.Clear(weights, 0, weights.Length);
            return false;
        }

        return true;
    }

    private static void NormalizeWeights(double[] rawWeights, double[] normalizedWeights)
    {
        double positive = 0.0;
        double negative = 0.0;
        for (int i = 0; i < rawWeights.Length; i++)
        {
            if (rawWeights[i] > 0.0)
            {
                positive += rawWeights[i];
            }
            else
            {
                negative -= rawWeights[i];
            }
        }

        double scale = Math.Max(positive, negative);
        if (scale <= 1e-9)
        {
            Array.Clear(normalizedWeights, 0, normalizedWeights.Length);
            return;
        }

        for (int i = 0; i < rawWeights.Length; i++)
        {
            normalizedWeights[i] = rawWeights[i] / scale;
        }
    }

    private static int CountDistinctProjectedPositions(double[] projected, double tolerance)
    {
        if (projected.Length == 0)
        {
            return 0;
        }

        var sorted = new double[projected.Length];
        Array.Copy(projected, sorted, projected.Length);
        Array.Sort(sorted);

        int count = 1;
        double last = sorted[0];
        for (int i = 1; i < sorted.Length; i++)
        {
            if (Math.Abs(sorted[i] - last) > tolerance)
            {
                count++;
                last = sorted[i];
            }
        }

        return count;
    }

    private static bool TrySolveLinearSystem(double[,] matrix, double[] rhs, out double[] solution, out double stabilityRatio)
    {
        int n = rhs.Length;
        solution = new double[n];
        stabilityRatio = 0.0;

        var augmented = new double[n, n + 1];
        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
            {
                augmented[row, col] = matrix[row, col];
            }
            augmented[row, n] = rhs[row];
        }

        double maxPivot = 0.0;
        double minPivot = double.PositiveInfinity;

        for (int pivot = 0; pivot < n; pivot++)
        {
            int bestRow = pivot;
            double bestAbs = Math.Abs(augmented[pivot, pivot]);
            for (int row = pivot + 1; row < n; row++)
            {
                double candidate = Math.Abs(augmented[row, pivot]);
                if (candidate > bestAbs)
                {
                    bestAbs = candidate;
                    bestRow = row;
                }
            }

            if (bestAbs <= 1e-12)
            {
                return false;
            }

            if (bestRow != pivot)
            {
                for (int col = pivot; col <= n; col++)
                {
                    (augmented[pivot, col], augmented[bestRow, col]) = (augmented[bestRow, col], augmented[pivot, col]);
                }
            }

            maxPivot = Math.Max(maxPivot, bestAbs);
            minPivot = Math.Min(minPivot, bestAbs);

            double pivotValue = augmented[pivot, pivot];
            for (int row = pivot + 1; row < n; row++)
            {
                double factor = augmented[row, pivot] / pivotValue;
                if (Math.Abs(factor) <= 0.0)
                {
                    continue;
                }

                for (int col = pivot; col <= n; col++)
                {
                    augmented[row, col] -= factor * augmented[pivot, col];
                }
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = augmented[row, n];
            for (int col = row + 1; col < n; col++)
            {
                sum -= augmented[row, col] * solution[col];
            }

            double pivotValue = augmented[row, row];
            if (Math.Abs(pivotValue) <= 1e-12)
            {
                return false;
            }

            solution[row] = sum / pivotValue;
        }

        stabilityRatio = maxPivot > 0.0 ? minPivot / maxPivot : 0.0;
        return true;
    }
}
