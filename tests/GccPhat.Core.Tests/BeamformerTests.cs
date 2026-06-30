using System;
using GccPhat.Core;
using Xunit;

namespace GccPhat.Core.Tests;

public class BeamformerTests
{
    [Fact]
    public void Process_AppliesPerChannelWeights()
    {
        var beamformer = new Beamformer(8, 48000);
        double[][] channels =
        {
            new double[] { 1, 0, 0, 0, 0, 0, 0, 0 },
            new double[] { 0.5, 0, 0, 0, 0, 0, 0, 0 }
        };
        double[] delays = { 0, 0 };
        double[] weights = { 2.0, -1.0 };
        var output = new double[8];

        beamformer.Process(channels, delays, weights, output);

        Assert.True(Math.Abs(output[0] - 1.5) < 1e-9, $"Expected 1.5, got {output[0]}.");
        for (int i = 1; i < output.Length; i++)
        {
            Assert.True(Math.Abs(output[i]) < 1e-9, $"Expected 0 at sample {i}, got {output[i]}.");
        }
    }

    [Fact]
    public void TryBuildWeights_TwoMicAxis_BuildsOrder1Difference()
    {
        double[] micX = { -0.05, 0.05 };
        double[] micY = { 0.0, 0.0 };
        var weights = new double[2];

        bool ok = DifferentialBeamformerDesigner.TryBuildWeights(micX, micY, 0.0, weights, out DifferentialBeamformerDesign design);

        Assert.True(ok);
        Assert.Equal(1, design.Order);
        Assert.Equal(1, design.CandidateOrder);
        Assert.True(Math.Abs(weights[0] + 1.0) < 1e-9, $"Expected -1, got {weights[0]}.");
        Assert.True(Math.Abs(weights[1] - 1.0) < 1e-9, $"Expected 1, got {weights[1]}.");
        Assert.True(Math.Abs(design.ApertureMeters - 0.1) < 1e-9, $"Expected 0.1 m spacing, got {design.ApertureMeters}.");
    }

    [Fact]
    public void TryBuildWeights_ThreeAlignedAxis_BuildsOrder2Difference()
    {
        double[] micX = { -0.05, 0.0, 0.05 };
        double[] micY = { 0.0, 0.0, 0.0 };
        var weights = new double[3];

        bool ok = DifferentialBeamformerDesigner.TryBuildWeights(micX, micY, 0.0, weights, out DifferentialBeamformerDesign design);

        Assert.True(ok);
        Assert.Equal(2, design.Order);
        Assert.Equal(2, design.CandidateOrder);
        Assert.True(Math.Abs(weights[0] - 0.5) < 1e-9, $"Expected 0.5, got {weights[0]}.");
        Assert.True(Math.Abs(weights[1] + 1.0) < 1e-9, $"Expected -1, got {weights[1]}.");
        Assert.True(Math.Abs(weights[2] - 0.5) < 1e-9, $"Expected 0.5, got {weights[2]}.");
    }

    [Fact]
    public void TryBuildWeights_SquareArraySideProjection_LimitsOrderToGeometry()
    {
        double[] micX = { -0.05, 0.05, 0.05, -0.05 };
        double[] micY = { -0.05, -0.05, 0.05, 0.05 };
        var weights = new double[4];

        bool ok = DifferentialBeamformerDesigner.TryBuildWeights(micX, micY, 0.0, weights, out DifferentialBeamformerDesign design);

        Assert.True(ok);
        Assert.Equal(1, design.Order);
        Assert.Equal(1, design.CandidateOrder);
        Assert.Equal(2, design.DistinctProjectedPositions);
    }

    [Fact]
    public void TryBuildWeights_FailsWithoutSteeringAxisSpan()
    {
        double[] micX = { -0.05, 0.05 };
        double[] micY = { 0.0, 0.0 };
        var weights = new double[2];

        bool ok = DifferentialBeamformerDesigner.TryBuildWeights(micX, micY, 90.0, weights, out DifferentialBeamformerDesign design);

        Assert.False(ok);
        Assert.Equal(0, design.Order);
        Assert.Equal(0, design.CandidateOrder);
        Assert.All(weights, weight => Assert.True(Math.Abs(weight) < 1e-12));
    }
}
