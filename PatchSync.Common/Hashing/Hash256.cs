using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PatchSync.Common.Hashing;

/// <summary>
/// A 256-bit (32-byte) hash value that can be used as a dictionary key.
/// Optimized for fast equality comparison and hashing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Hash256 : IEquatable<Hash256>
{
    // Store as 4 ulongs for fast comparison
    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    public Hash256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
            throw new ArgumentException("Hash must be exactly 32 bytes", nameof(bytes));

        _a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        _c = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        _d = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    public Hash256(byte[] bytes) : this(bytes.AsSpan()) { }

    /// <summary>
    /// Creates a Hash256 from a hex string.
    /// </summary>
    public static Hash256 FromHexString(string hex)
    {
        if (hex.Length != 64)
            throw new ArgumentException("Hex string must be exactly 64 characters", nameof(hex));

        return new Hash256(Convert.FromHexString(hex));
    }

    /// <summary>
    /// Copies the hash bytes to the destination span.
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 32)
            throw new ArgumentException("Destination must be at least 32 bytes", nameof(destination));

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _d);
    }

    /// <summary>
    /// Returns the hash as a byte array.
    /// </summary>
    public byte[] ToByteArray()
    {
        var bytes = new byte[32];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Returns the hash as a lowercase hex string.
    /// </summary>
    public string ToHexString() => Convert.ToHexString(ToByteArray()).ToLowerInvariant();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Hash256 other) =>
        _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is Hash256 other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        // Use first 4 bytes as hash code (good distribution for SHA256)
        return (int)_a;
    }

    public override string ToString() => ToHexString();

    public static bool operator ==(Hash256 left, Hash256 right) => left.Equals(right);
    public static bool operator !=(Hash256 left, Hash256 right) => !left.Equals(right);

    /// <summary>
    /// Empty/default hash (all zeros).
    /// </summary>
    public static readonly Hash256 Empty = default;
}
