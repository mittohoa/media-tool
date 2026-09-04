using MediaTool.Core.Imaging;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Reading the shape of the difference between two images.
///
/// The claim being tested is the one the reviewer will rely on: that re-encoding and a
/// second exposure produce differences of the same *size* but opposite *shape*, and that the
/// shape is what tells them apart. If that does not hold, the heatmap is decoration.
/// </summary>
public class DifferenceMapTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void AnImageComparedWithItselfShowsNothing()
    {
        string path = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 4);

        var result = DifferenceMap.Compare(path, path);

        Assert.Equal(DifferenceKind.Identical, result.Kind);
        Assert.True(result.MeanDifference < 1.0);
        Assert.False(result.SuggestsSeparateExposure);
    }

    [Fact]
    public void RecompressionSpreadsItsDifferenceEvenly()
    {
        // The same picture through a heavier encoder: error everywhere, concentrated nowhere.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 9, width: 640, height: 480);
        string resaved = _workspace.ReencodeJpeg(original, _workspace.PathIn("b.jpg"), quality: 35);

        var result = DifferenceMap.Compare(original, resaved);

        Assert.True(result.Concentration < 0.50,
            $"codec noise should not gather in one place (concentration {result.Concentration:F2})");
        Assert.False(result.SuggestsSeparateExposure,
            $"a re-encode must not read as a separate exposure (got {result.Kind})");
    }

    [Fact]
    public void SomethingMovingInTheFrameConcentratesTheDifference()
    {
        // Stand-in for a burst frame: the scene holds still and one region changes, which is
        // what a person shifting between two shutter presses looks like.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 9, width: 640, height: 480);
        string moved = _workspace.WithPatch(original, _workspace.PathIn("b.jpg"),
            x: 260, y: 180, width: 110, height: 110);

        var result = DifferenceMap.Compare(original, moved);

        Assert.True(result.Concentration > 0.50,
            $"a localised change should concentrate (concentration {result.Concentration:F2})");
        Assert.True(result.SuggestsSeparateExposure,
            $"a localised change should be flagged for review (got {result.Kind})");
    }

    [Fact]
    public void ShapeSeparatesTheTwoCasesEvenAtSimilarMagnitude()
    {
        // The whole premise: the same amount of difference means opposite things depending
        // on how it is spread, so magnitude alone cannot decide.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 9, width: 640, height: 480);
        string resaved = _workspace.ReencodeJpeg(original, _workspace.PathIn("b.jpg"), quality: 35);
        string moved = _workspace.WithPatch(original, _workspace.PathIn("c.jpg"),
            x: 260, y: 180, width: 110, height: 110);

        var codec = DifferenceMap.Compare(original, resaved);
        var motion = DifferenceMap.Compare(original, moved);

        Assert.True(motion.Concentration > codec.Concentration + 0.15,
            $"shape must separate them: codec {codec.Concentration:F2} vs motion {motion.Concentration:F2}");
    }

    [Fact]
    public void UnrelatedPhotographsAreReportedAsSuch()
    {
        string a = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 1, width: 640, height: 480);
        string b = _workspace.WriteJpeg(_workspace.PathIn("b.jpg"), seed: 2, width: 640, height: 480);

        var result = DifferenceMap.Compare(a, b);

        Assert.True(result.SuggestsSeparateExposure);
    }

    [Fact]
    public void ABrightnessShiftIsNotMistakenForAChangedScene()
    {
        // Matching brightness first is what stops a lightened copy lighting up the whole map.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 6, width: 640, height: 480);
        string brighter = _workspace.WithBrightness(original, _workspace.PathIn("b.jpg"), delta: 38);

        var result = DifferenceMap.Compare(original, brighter);

        Assert.False(result.SuggestsSeparateExposure,
            $"a brightness change must not read as a different frame (got {result.Kind}, " +
            $"mean {result.MeanDifference:F1}, concentration {result.Concentration:F2})");
    }

    [Fact]
    public void AVerdictOfNoDifferenceComesWithADarkMap()
    {
        // The picture and the words have to agree. A map that glows beside "no visible
        // difference" teaches the reviewer to distrust both.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 7, width: 640, height: 480);
        string resaved = _workspace.ReencodeJpeg(original, _workspace.PathIn("b.jpg"), quality: 95);

        var result = DifferenceMap.Compare(original, resaved);
        Assert.Equal(DifferenceKind.Identical, result.Kind);

        // Not that every pixel is black - a handful always differ - but that the map reads
        // as dark, which is what the reviewer glancing at it actually takes in.
        int lit = 0;
        for (int i = 0; i < result.Bgra.Length; i += 4)
            if (result.Bgra[i + 2] > 80) lit++;

        double litShare = (double)lit / (result.Width * result.Height);
        Assert.True(litShare < 0.01, $"an identical verdict must not light the map ({litShare:P1} lit)");
    }

    [Fact]
    public void ChangedAreaIsReportedButDoesNotDecideOnItsOwn()
    {
        // The measurement shown beside the verdict has to be honest about its own limits.
        // A localised change covers less of the frame than codec noise does, so ranking by
        // area alone would call the second exposure the smaller difference of the two.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 9, width: 640, height: 480);
        string resaved = _workspace.ReencodeJpeg(original, _workspace.PathIn("b.jpg"), quality: 35);
        string moved = _workspace.WithPatch(original, _workspace.PathIn("c.jpg"),
            x: 260, y: 180, width: 110, height: 110);

        var codec = DifferenceMap.Compare(original, resaved);
        var motion = DifferenceMap.Compare(original, moved);

        Assert.InRange(codec.ChangedArea, 0, 1);
        Assert.InRange(motion.ChangedArea, 0, 1);

        // The patch is a tenth of the frame at most, so its area cannot outgrow the codec's.
        Assert.True(motion.ChangedArea < 0.25,
            $"a localised change should touch little of the frame (got {motion.ChangedArea:P1})");

        // And yet it is the one that matters - which is why shape, not area, casts the vote.
        Assert.True(motion.SuggestsSeparateExposure);
        Assert.False(codec.SuggestsSeparateExposure);
    }

    [Fact]
    public void TheStatedMeasurementsNeverContradictTheMap()
    {
        // A downscaled copy is still the same picture, but resampling leaves speckles along
        // hard edges. The line under the map must own them rather than claim a clean slate,
        // because the reviewer can see them.
        string original = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 5, width: 1280, height: 960);
        string small = _workspace.WriteJpeg(_workspace.PathIn("b.jpg"), seed: 5, width: 320, height: 240);

        var result = DifferenceMap.Compare(original, small);

        if (result.ChangedArea > 0)
            Assert.DoesNotContain("nothing above", result.Measurements);
        else
            Assert.Contains("nothing above", result.Measurements);
    }

    [Fact]
    public void TheHeatmapIsProducedAtTheReferenceSize()
    {
        string a = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 3, width: 640, height: 480);
        string b = _workspace.WriteJpeg(_workspace.PathIn("b.jpg"), seed: 3, width: 320, height: 240);

        var result = DifferenceMap.Compare(a, b);

        // The smaller image is resampled onto the reference grid rather than the other way
        // round, so the map always lines up with the picture it is shown beside.
        Assert.Equal(result.Width * result.Height * 4, result.Bgra.Length);
        Assert.True(result.Width > result.Height, "a landscape reference should give a landscape map");
    }
}
