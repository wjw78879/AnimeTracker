using System.Text;
using System.Web;
using System.Xml;
using System.Security.Cryptography;

namespace UNISLAND.AnimeTracker
{
    internal class AnimeTracker : IDisposable
    {
        struct Configs
        {
            public string TorrentPath { get; set; }
            public string ExecutablePath { get; set; }
            public bool EarlyTerminate { get; set; }
            public int FetchPeriodMinutes { get; set; }
            public int HttpRequestTimeoutMilliseconds { get; set; }
        }

        struct Track
        {
            public string Keywords { get; set; }
            public string Path { get; set; }
        }

        struct Anime
        {
            public string Hash { get; set; }
            public string Title { get; set; }
            public DateTime PublishDate { get; set; }
            public string TorrentUrl { get; set; }
            public ulong Size { get; set; }
        }

        readonly Configs m_configs;
        readonly HashSet<string> m_history;
        List<Track> m_tracks;

        readonly HttpClient m_client;
        readonly Downloader m_downloader;

        bool m_isFetching;

        public AnimeTracker()
        {
            m_configs = ReadConfigs();
            m_history = ReadHistory();
            m_tracks = ReadTracks();

            m_client = new HttpClient();
            m_client.Timeout = new TimeSpan(m_configs.HttpRequestTimeoutMilliseconds * 10000);
            m_downloader = new Downloader(m_configs.TorrentPath, m_configs.ExecutablePath, m_configs.HttpRequestTimeoutMilliseconds);

            m_isFetching = false;
        }
        public void Dispose()
        {
            m_client.Dispose();
            m_downloader.Dispose();
        }

        public void Run()
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            _ = FetchOnce();
            Task loopTask = FetchLoop(cts.Token);
            bool cancel = false;
            while (true)
            {
                string? cmd = Console.ReadLine();
                if (!string.IsNullOrEmpty(cmd))
                {
                    switch (cmd)
                    {
                        case "help":
                            Console.WriteLine("help: show help");
                            Console.WriteLine("reload: reload tracks");
                            Console.WriteLine("fetch: fetch immediately");
                            Console.WriteLine("quit: quit anime tracker");
                            break;
                        case "reload":
                            m_tracks = ReadTracks();
                            break;
                        case "fetch":
                            if (m_isFetching)
                            {
                                Console.WriteLine("Fetching in progress. Please try again later.");
                            }
                            else
                            {
                                _ = FetchOnce();
                            }
                            break;
                        case "quit":
                            cts.Cancel();
                            cancel = true;
                            break;

                        default:
                            Console.WriteLine("type \"help\" to show help.");
                            break;
                    }
                }

                if (cancel)
                {
                    break;
                }
            }

            if (m_isFetching)
            {
                Console.Write("The program will exit after the fetching is complete...");
                while (m_isFetching)
                {

                }
            }
        }

        async Task FetchLoop(CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(new TimeSpan(0, m_configs.FetchPeriodMinutes, 0), token);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!m_isFetching)
                {
                    await FetchOnce();
                }
            }
        }

        async Task FetchOnce()
        {
            if (m_tracks.Count == 0)
            {
                Console.WriteLine("No tracks found.");
                return;
            }

            m_isFetching = true;
            List<string> hashList = new List<string>();
            int fails = 0;
            for (int i = 0; i < m_tracks.Count; i++)
            {
                Track track = m_tracks[i];
                Console.WriteLine($"Fetching {i + 1}/{m_tracks.Count}...");
                string url = "https://mikanani.me/RSS/Search?searchstr=" + HttpUtility.UrlEncode(track.Keywords);
                List<Anime> newAnime = await FetchAnimeList(url);
                foreach (Anime anime in newAnime)
                {
                    Console.WriteLine($"New anime: {anime.PublishDate}, {SizeToString(anime.Size, 1)}, {anime.Title}");

                    hashList.Add(anime.Hash);
                    m_history.Add(anime.Hash);
                    (bool success, string message) = await m_downloader.Download(anime.TorrentUrl, track.Path);
                    if (!success)
                    {
                        Console.WriteLine($"Download failed: {message}");
                        fails += 1;
                    }
                }
            }

            WriteHistory(hashList);
            Console.WriteLine($"Fetch complete. {hashList.Count} new anime found, {hashList.Count - fails} success, {fails} failed.");
            Console.WriteLine();
            m_isFetching = false;
        }

        async Task<List<Anime>> FetchAnimeList(string url)
        {
            List<Anime> list = new List<Anime>();
            try
            {
                Stream content = await m_client.GetStreamAsync(url);

                using XmlReader reader = XmlReader.Create(content, new XmlReaderSettings { Async = true });
                while (await reader.ReadAsync())
                {
                    if (reader.Name == "item")
                    {
                        Anime anime = await ReadAnime(reader.ReadSubtree());
                        if (!m_history.Contains(anime.Hash))
                        {
                            list.Add(anime);
                        }
                        else if (m_configs.EarlyTerminate)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fetching url \"{url}\" failed: {ex.Message}");
            }

            return list;
        }

        static async Task<Anime> ReadAnime(XmlReader subTree)
        {
            Anime anime = new Anime();
            while (await subTree.ReadAsync())
            {
                if (subTree.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (subTree.Name == "guid" && await subTree.ReadAsync() && subTree.NodeType == XmlNodeType.Text)
                {
                    anime.Hash = Convert.ToHexString(SHA1.HashData(Encoding.Unicode.GetBytes(subTree.Value)));
                }
                else if (subTree.Name == "title" && await subTree.ReadAsync() && subTree.NodeType == XmlNodeType.Text)
                {
                    anime.Title = subTree.Value;
                }
                else if (subTree.Name == "pubDate" && await subTree.ReadAsync() && subTree.NodeType == XmlNodeType.Text)
                {
                    anime.PublishDate = DateTime.Parse(subTree.Value);
                }
                else if (subTree.Name == "enclosure")
                {
                    string? url = subTree.GetAttribute("url");
                    if (url != null)
                    {
                        anime.TorrentUrl = url;
                    }
                }
                else if (subTree.Name == "contentLength" && await subTree.ReadAsync() && subTree.NodeType == XmlNodeType.Text)
                {
                    anime.Size = ulong.Parse(subTree.Value);
                }
            }
            return anime;
        }

        static Configs ReadConfigs()
        {
            Configs configs = new Configs
            {
                TorrentPath = "C:\\Users\\wjw11\\Downloads",
                ExecutablePath = "C:\\Program Files\\BitComet\\BitComet.exe",
                EarlyTerminate = true,
                FetchPeriodMinutes = 10,
                HttpRequestTimeoutMilliseconds = 30000
            };

            if (File.Exists("config.txt"))
            {
                foreach (string line in File.ReadAllLines("config.txt"))
                {
                    string[] parts = line.Split('=', StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        string key = parts[0];
                        string value = parts[1];
                        if (string.Compare(key, "torrentPath", true) == 0)
                        {
                            configs.TorrentPath = value;
                        }
                        else if (string.Compare(key, "executablePath", true) == 0)
                        {
                            configs.ExecutablePath = value;
                        }
                        else if (string.Compare(key, "earlyTerminate", true) == 0)
                        {
                            if (bool.TryParse(value, out bool et))
                            {
                                configs.EarlyTerminate = et;
                            }
                        }
                        else if (string.Compare(key, "fetchPeriodMinutes", true) == 0)
                        {
                            if (int.TryParse(value, out int p) && p > 0)
                            {
                                configs.FetchPeriodMinutes = p;
                            }
                        }
                        else if (string.Compare(key, "httpRequestTimeoutMilliseconds", true) == 0)
                        {
                            if (int.TryParse(value, out int t) && t > 0)
                            {
                                configs.HttpRequestTimeoutMilliseconds = t;
                            }
                        }
                    }
                }
            }

            return configs;
        }

        static void WriteHistory(List<string> newHistory)
        {
            using FileStream fs = new FileStream("history.txt", FileMode.Append);
            using StreamWriter writer = new StreamWriter(fs);
            foreach (string hash in newHistory)
            {
                writer.WriteLine(hash);
            }
        }

        static HashSet<string> ReadHistory()
        {
            HashSet<string> history = new HashSet<string>();
            if (File.Exists("history.txt"))
            {
                using FileStream fs = new FileStream("history.txt", FileMode.Open);
                using StreamReader reader = new StreamReader(fs);
                try
                {
                    while (true)
                    {
                        string? hash = reader.ReadLine();
                        if (hash == null)
                        {
                            break;
                        }

                        if (hash != string.Empty)
                        {
                            history.Add(hash);
                        }
                    }
                }
                catch (Exception) { }
            }

            return history;
        }

        static List<Track> ReadTracks()
        {
            List<Track> tracks = new List<Track>();
            if (File.Exists("tracks.txt"))
            {
                using FileStream fs = new FileStream("tracks.txt", FileMode.Open);
                using StreamReader reader = new StreamReader(fs);
                while (true)
                {
                    string? keywords;
                    do
                    {
                        keywords = reader.ReadLine();
                    } while (keywords != null && keywords == "");

                    if (string.IsNullOrEmpty(keywords))
                    {
                        break;
                    }

                    string? path = reader.ReadLine();
                    if (string.IsNullOrEmpty(path))
                    {
                        break;
                    }

                    Console.WriteLine($"Loaded track:\nKeywords: {keywords}\nPath: {path}");
                    tracks.Add(new Track { Keywords = keywords, Path = path });
                }
            }

            Console.WriteLine();

            return tracks;
        }

        static readonly string[] SIZE_UNITS = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

        static string SizeToString(ulong size, int decimals)
        {
            int i = 0;
            decimal s = size;
            for (; i < SIZE_UNITS.Length - 1; i++)
            {
                if (s < 1024)
                {
                    break;
                }
                else
                {
                    s /= 1024;
                }
            }

            s = decimal.Round(s, decimals, MidpointRounding.AwayFromZero);
            return s.ToString() + SIZE_UNITS[i];
        }
    }
}
