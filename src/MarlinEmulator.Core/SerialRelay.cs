namespace MarlinEmulator.Core;

public static class SerialRelay
{
    // Windows virtual COM drivers may complete a read with zero bytes while idle.
    // Unlike TCP this does not mean the serial connection has ended.
    public static async Task CopyAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count;
            try { count = await source.ReadAsync(buffer, token); }
            catch (TimeoutException) { count = 0; }
            if (count == 0)
            {
                await Task.Delay(20, token);
                continue;
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
