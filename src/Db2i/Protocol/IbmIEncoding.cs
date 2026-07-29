using System.Text;

namespace Db2i.Protocol;

internal static class IbmIEncoding
{
    static IbmIEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static Encoding Get(int ccsid)
    {
        if (ccsid is 1200 or 13488)
        {
            return new UnicodeEncoding(
                bigEndian: true,
                byteOrderMark: false,
                throwOnInvalidBytes: true);
        }

        const string ibmPrefix = "IBM";
        try
        {
            return GetStrictEncoding(ccsid);
        }
        catch (Exception numericException)
            when (numericException is ArgumentException or NotSupportedException)
        {
            // Windows/.NET assigns 20xxx identifiers to several EBCDIC pages:
            // IBM i CCSID 280, for example, is exposed under the name IBM280
            // and numeric code page 20280.
            foreach (var name in new[] { $"{ibmPrefix}{ccsid:D3}", $"{ibmPrefix}{ccsid:D5}" })
            {
                try
                {
                    return GetStrictEncoding(name);
                }
                catch (Exception namedException)
                    when (namedException is ArgumentException or NotSupportedException)
                {
                }
            }

            throw new Db2iException(
                $"IBM i CCSID {ccsid} is not supported by this .NET runtime.",
                Db2iErrorKind.UnsupportedCcsid,
                numericException);
        }
    }

    internal static byte[] Encode(int ccsid, string value) => Get(ccsid).GetBytes(value);

    internal static string Decode(int ccsid, ReadOnlySpan<byte> value)
        => DecodeExact(ccsid, value).TrimEnd();

    internal static string DecodeExact(int ccsid, ReadOnlySpan<byte> value)
        => Get(ccsid).GetString(value);

    private static Encoding GetStrictEncoding(int codePage)
        => Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

    private static Encoding GetStrictEncoding(string name)
        => Encoding.GetEncoding(
            name,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
}
