using System.Security.Cryptography;

namespace Merit.Domain.Ids;

/// <summary>
/// Application-generated primary keys. SQL Server orders a UNIQUEIDENTIFIER by bytes 10-15 first, so the time-ordered
/// part (milliseconds since 1970, big-endian) goes there and the other ten bytes are random: inserts stay in index
/// order, yet an id cannot be guessed or used to count records. NEWSEQUENTIALID is not used because its next value can
/// be guessed. The stored procedures generate the same layout when they create rows themselves.
/// </summary>
public static class TimeOrderedId
{
    public static Guid New() => New(DateTimeOffset.UtcNow);

    public static Guid New(DateTimeOffset now)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[..10]);
        long ms = now.ToUnixTimeMilliseconds();
        for (int i = 15; i >= 10; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }

        return new Guid(bytes);
    }

    /// <summary>The time encoded in an id made by <see cref="New()"/>.</summary>
    public static DateTimeOffset TimeOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        long ms = 0;
        for (int i = 10; i <= 15; i++)
        {
            ms = (ms << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
