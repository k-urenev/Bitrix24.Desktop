using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Rest;

namespace Bitrix24.Desktop.Core.Tests;

public class StreamLimitTests
{
    [Fact]
    public async Task Read_stops_when_the_stream_passes_the_limit()
    {
        var stream = new MemoryStream(new byte[32]);

        var error = await Assert.ThrowsAsync<BitrixException>(() => StreamLimit.ReadAsync(stream, 16, CancellationToken.None));

        Assert.Equal("Файл слишком большой для превью.", error.Message);
    }

    [Fact]
    public async Task Read_returns_a_stream_that_fits()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };

        var read = await StreamLimit.ReadAsync(new MemoryStream(bytes), 16, CancellationToken.None);

        Assert.Equal(bytes, read);
    }
}
