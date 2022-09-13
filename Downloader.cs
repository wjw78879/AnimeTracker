using System.Diagnostics;

namespace UNISLAND.AnimeTracker
{
    internal class Downloader : IDisposable
    {
        readonly HttpClient m_client;

        readonly string m_torrentPath;
        readonly string m_executablePath;

        public Downloader(string torrentPath, string executablePath, int timeoutMilliseconds)
        {
            m_client = new HttpClient();
            m_client.Timeout = new TimeSpan(timeoutMilliseconds * 10000);

            m_torrentPath = torrentPath;
            m_executablePath = executablePath;
        }

        public void Dispose()
        {
            m_client.Dispose();
        }

        public async Task<(bool, string)> Download(string url, string path)
        {
            try
            {
                HttpResponseMessage response = await m_client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                System.Net.Http.Headers.ContentDispositionHeaderValue? contentDisposition = response.Content.Headers.ContentDisposition;
                if (contentDisposition == null)
                {
                    return (false, "The URL is not a file.");
                }

                string? fileName = contentDisposition.FileNameStar;
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = contentDisposition.FileNameStar;
                }

                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"unknown{url.GetHashCode()}.torrent";
                }
                else
                {
                    if (!fileName.EndsWith(".torrent"))
                    {
                        return (false, "This is not a torrent file.");
                    }
                }

                string torrentPath = Path.Combine(m_torrentPath, fileName);

                await File.WriteAllBytesAsync(torrentPath, await response.Content.ReadAsByteArrayAsync());

                try
                {
                    using Process process = new Process();
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.FileName = m_executablePath;
                    process.StartInfo.Arguments = $"\"{torrentPath}\" -o \"{path}\" -s";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                }
                catch (Exception ex)
                {
                    return (false, $"Server execute error: {ex.Message}");
                }

                return (true, "Successfully handled download request.");
            }
            catch (HttpRequestException ex)
            {
                return (false, $"Http error: {ex.Message}");
            }
        }
    }
}
