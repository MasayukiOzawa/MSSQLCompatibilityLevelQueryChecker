using System.Text;

namespace MssqlCompatCheck.Analysis;

internal static class SqlTextReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    public static async Task<string> ReadAsync(
        string path,
        string? encodingName,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var (encoding, preambleLength) = DetectEncoding(bytes, encodingName);
        return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes, string? encodingName)
    {
        if (HasPrefix(bytes, 0xEF, 0xBB, 0xBF))
        {
            return (StrictUtf8, 3);
        }

        if (HasPrefix(bytes, 0xFF, 0xFE))
        {
            return (StrictUtf16LittleEndian, 2);
        }

        if (HasPrefix(bytes, 0xFE, 0xFF))
        {
            return (StrictUtf16BigEndian, 2);
        }

        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return (StrictUtf8, 0);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var namedEncoding = Encoding.GetEncoding(
            encodingName,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        return (namedEncoding, 0);
    }

    private static bool HasPrefix(byte[] bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && prefix.AsSpan().SequenceEqual(bytes.AsSpan(0, prefix.Length));
}
