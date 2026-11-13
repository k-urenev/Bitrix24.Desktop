using Bitrix24.Desktop.Core.Rest;

namespace Bitrix24.Desktop.Core;

public static class StreamLimit
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > maxBytes)
                throw new BitrixException("Файл слишком большой для превью.", null, false, false, null);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
