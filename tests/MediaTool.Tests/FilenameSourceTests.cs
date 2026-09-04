using MediaTool.Core.Metadata;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Reading a capture time out of a filename.
///
/// This exists because the files most likely to have no embedded metadata are the ones that
/// came through a messaging app — which strips EXIF deliberately, and then writes the
/// timestamp into the name. Without this the tool sees them as photos with no history and
/// ranks them accordingly.
/// </summary>
public class FilenameSourceTests
{
    [Theory]
    [InlineData("IMG_20210501_172347.jpg", 2021, 5, 1, 17, 23, 47)]
    [InlineData("VID_20210501_172347.mp4", 2021, 5, 1, 17, 23, 47)]
    [InlineData("PXL_20210501_172347123.jpg", 2021, 5, 1, 17, 23, 47)]
    [InlineData("20210501_172347.jpg", 2021, 5, 1, 17, 23, 47)]
    [InlineData("viber_image_2021-05-01_17-23-47.jpg", 2021, 5, 1, 17, 23, 47)]
    [InlineData("photo_2021-05-01_17-23-47.jpg", 2021, 5, 1, 17, 23, 47)]
    [InlineData("Screenshot_20210501-172347.png", 2021, 5, 1, 17, 23, 47)]
    public void ATimestampInTheNameIsRecovered(string name, int y, int mo, int d, int h, int mi, int s)
        => Assert.Equal(new DateTime(y, mo, d, h, mi, s), FilenameDate.Read(name).Taken);

    [Fact]
    public void WhatsAppNamesCarryTheDateWithoutATime()
    {
        var facts = FilenameDate.Read("IMG-20210501-WA0012.jpg");
        Assert.Equal(new DateTime(2021, 5, 1), facts.Taken);
        Assert.Equal(MediaSource.Messaging, facts.Source);
    }

    [Fact]
    public void MessengerNamesCarryUnixMilliseconds()
    {
        // FB_IMG_<ms>: the moment the message was handled, not the shutter — which is why
        // this value never reaches the guard that separates exposures.
        var facts = FilenameDate.Read("FB_IMG_1619875427123.jpg");
        Assert.NotNull(facts.Taken);
        Assert.Equal(2021, facts.Taken!.Value.Year);
        Assert.Equal(MediaSource.Messaging, facts.Source);
    }

    [Theory]
    [InlineData(@"Pictures\Zalo\IMG_1234.jpg", MediaSource.Messaging)]
    [InlineData(@"Pictures\Messenger\something.jpg", MediaSource.Messaging)]
    [InlineData(@"DCIM\Camera\IMG_0001.jpg", MediaSource.Camera)]
    [InlineData(@"Downloads\photo.jpg", MediaSource.Download)]
    [InlineData(@"Pictures\Screenshots\a.png", MediaSource.Screenshot)]
    public void TheFolderIdentifiesTheApplicationWhenTheNameDoesNot(string path, MediaSource expected)
        => Assert.Equal(expected, FilenameDate.Read(path).Source);

    [Theory]
    [InlineData("holiday.jpg")]
    [InlineData("DSC_0001.jpg")]
    [InlineData("_MG_8676.jpg")]
    [InlineData("scan-00000001.jpg")]
    public void ANameWithNoTimestampYieldsNothingRatherThanAGuess(string name)
        => Assert.Null(FilenameDate.Read(name).Taken);

    [Theory]
    // A resolution, a phone model, an ID — digit runs that are not dates.
    [InlineData("photo_19201080_12345678.jpg")]
    [InlineData("IMG_00000000_000000.jpg")]
    [InlineData("x_29991301_259999.jpg")]
    public void ImplausibleDigitsAreRejected(string name)
        => Assert.Null(FilenameDate.Read(name).Taken);

    [Fact]
    public void AFilenameDateIsKeptSeparateFromTheAuthoritativeOne()
    {
        // The whole point of the separation: a name-derived time must never be mistaken for
        // something the camera recorded, because only the latter can prove two frames are
        // different exposures.
        var metadata = new ImageMetadata { FilenameDate = new DateTime(2021, 5, 1) };

        Assert.Null(metadata.DateTaken);
        Assert.Equal(new DateTime(2021, 5, 1), metadata.BestDate);
        Assert.False(metadata.DependsOnSidecar);
    }
}
