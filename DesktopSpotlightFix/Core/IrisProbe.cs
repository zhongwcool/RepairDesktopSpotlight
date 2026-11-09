namespace DesktopSpotlightFix.Core;

public sealed class IrisProbe : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        UseProxy = true,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public IrisProbe()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopSpotlightFix/1.0");
    }

    public async Task<IrisProbeResult> ProbeAsync(string? cachedUri, CancellationToken cancellationToken)
    {
        var uri = string.IsNullOrWhiteSpace(cachedUri)
            ? IrisUriBuilder.BuildFresh(IrisUriBuilder.CurrentCountry(), IrisUriBuilder.CurrentLocale(), IrisUriBuilder.LocalOsVersion())
            : IrisUriBuilder.WithNewAsid(cachedUri);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var text = await ReadLimitedAsync(response.Content, 2_000_000, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new IrisProbeResult(false, false, "接口返回 HTTP " + (int)response.StatusCode + "。");
            if (IrisUriBuilder.ContainsImageFields(text))
                return new IrisProbeResult(true, true, "可访问，且正在返回桌面壁纸内容。");
            return new IrisProbeResult(true, false, "可访问，但没有返回壁纸内容。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new IrisProbeResult(false, false, "访问超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new IrisProbeResult(false, false, "无法访问：" + ex.Message);
        }
    }

    public async Task<string> DownloadTextAsync(string uri, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(uri, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response.Content, 5_000_000, cts.Token).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(string uri, string destination, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var http = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await http.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false);
            if (read <= 0)
                break;
            total += read;
            if (total > 30_000_000)
                throw new IOException("单张图片超过 30MB，已停止下载。");
            await file.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < maxBytes)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;
            var allowed = Math.Min(read, maxBytes - (int)buffer.Length);
            buffer.Write(chunk, 0, allowed);
            if (allowed < read)
                break;
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
