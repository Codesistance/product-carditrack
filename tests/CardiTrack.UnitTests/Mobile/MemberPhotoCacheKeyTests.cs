using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.UnitTests.Mobile;

public class MemberPhotoCacheKeyTests
{
    private const string Member = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    private static Uri Signed(string objectName, string signature) =>
        new($"https://storage.googleapis.com/photos-bucket/{objectName}?X-Goog-Signature={signature}&X-Goog-Expires=900");

    [Fact]
    public void AReSignedLinkToTheSamePhotoHasTheSameKey()
    {
        // The whole point: the signature changes every few minutes, the photo does not.
        var first = MemberPhotoCacheKey.For(Signed($"members/{Member}/a1b2.jpg", "aaa"));
        var resigned = MemberPhotoCacheKey.For(Signed($"members/{Member}/a1b2.jpg", "bbb"));

        Assert.NotNull(first);
        Assert.Equal(first, resigned);
    }

    [Fact]
    public void ANewPhotoForTheSameMemberHasANewFileInTheSameFolder()
    {
        var old = MemberPhotoCacheKey.For(Signed($"members/{Member}/a1b2.jpg", "aaa"))!;
        var replaced = MemberPhotoCacheKey.For(Signed($"members/{Member}/c3d4.jpg", "aaa"))!;

        Assert.Equal(old.Folder, replaced.Folder);
        Assert.NotEqual(old.FileName, replaced.FileName);
        Assert.Equal(Guid.Parse(Member).ToString("N"), old.Folder);
        Assert.EndsWith(".jpg", old.FileName);
    }

    [Fact]
    public void APathWithoutAMemberGoesToTheSharedFolder()
    {
        var key = MemberPhotoCacheKey.For(new Uri("https://example.com/avatars/x.png"))!;

        Assert.Equal(MemberPhotoCacheKey.SharedFolder, key.Folder);
        Assert.EndsWith(".png", key.FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file:///data/photo.jpg")]
    public void OnlyWebAddressesAreCached(string? url)
    {
        Assert.Null(MemberPhotoCacheKey.For(url is null ? null : new Uri(url)));
    }
}
