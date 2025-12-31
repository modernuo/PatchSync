using PatchSync.Common.Hashing;

namespace PatchSync.Tests;

public class Hash256Tests
{
    [Fact]
    public void Constructor_WithValidBytes_Succeeds()
    {
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        var hash = new Hash256(bytes);

        Assert.Equal(bytes, hash.ToByteArray());
    }

    [Fact]
    public void Constructor_WithInvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Hash256(new byte[16]));
        Assert.Throws<ArgumentException>(() => new Hash256(new byte[64]));
    }

    [Fact]
    public void FromHexString_WithValidHex_Succeeds()
    {
        var hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var hash = Hash256.FromHexString(hex);

        Assert.Equal(hex, hash.ToHexString());
    }

    [Fact]
    public void FromHexString_WithInvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Hash256.FromHexString("0123456789abcdef"));
    }

    [Fact]
    public void Equals_WithSameBytes_ReturnsTrue()
    {
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        var hash1 = new Hash256(bytes);
        var hash2 = new Hash256(bytes);

        Assert.Equal(hash1, hash2);
        Assert.True(hash1 == hash2);
        Assert.False(hash1 != hash2);
    }

    [Fact]
    public void Equals_WithDifferentBytes_ReturnsFalse()
    {
        var bytes1 = new byte[32];
        var bytes2 = new byte[32];
        new Random(42).NextBytes(bytes1);
        new Random(99).NextBytes(bytes2);

        var hash1 = new Hash256(bytes1);
        var hash2 = new Hash256(bytes2);

        Assert.NotEqual(hash1, hash2);
        Assert.False(hash1 == hash2);
        Assert.True(hash1 != hash2);
    }

    [Fact]
    public void GetHashCode_IsDeterministic()
    {
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        var hash1 = new Hash256(bytes);
        var hash2 = new Hash256(bytes);

        Assert.Equal(hash1.GetHashCode(), hash2.GetHashCode());
    }

    [Fact]
    public void CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<Hash256, string>();
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        var hash1 = new Hash256(bytes);
        dict[hash1] = "test";

        var hash2 = new Hash256(bytes);
        Assert.True(dict.ContainsKey(hash2));
        Assert.Equal("test", dict[hash2]);
    }

    [Fact]
    public void Empty_IsAllZeros()
    {
        var expected = new byte[32];
        Assert.Equal(expected, Hash256.Empty.ToByteArray());
    }

    [Fact]
    public void ToString_ReturnsHexString()
    {
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);

        var hash = new Hash256(bytes);

        Assert.Equal(hash.ToHexString(), hash.ToString());
    }
}
