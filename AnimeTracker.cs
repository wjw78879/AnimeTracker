using System.Text;
using System.Web;
using System.Xml;
using System.Security.Cryptography;
using System.ServiceModel.Syndication;
using System.Runtime.Serialization;

namespace UNISLAND.AnimeTracker
{
    internal class AnimeTracker : IDisposable
    {
        struct Configs
        {
            public string TorrentPath { get; set; }
            public string ExecutablePath { get; set; }
            public bool EarlyTerminate { get; set; }
            public int FetchPeriodSeconds { get; set; }
            public int HttpRequestTimeoutMilliseconds { get; set; }
            public int HttpRequestTries { get; set; }
            public bool SkipFailures { get; set; }
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

        [DataContract(Name = "torrent", Namespace = "https://mikanani.me/0.1/")]
        class MikanTorrent
        {
            [DataMember(IsRequired = true, Name = "link", Order = 0)]
            public string? Link;

            [DataMember(IsRequired = true, Name = "contentLength", Order = 1)]
            public ulong ContentLength;

            [DataMember(IsRequired = true, Name = "pubDate", Order = 2)]
            public DateTime PubDate;
        }

        readonly Configs m_configs;
        readonly HashSet<string> m_history;
        List<Track> m_tracks;

        readonly HttpClient m_client;
        readonly Downloader m_downloader;

        (DateTime, string) m_lastFetch;

        bool m_isFetching;
        int m_nextTrack;

        public AnimeTracker()
        {
            m_configs = ReadConfigs();
            m_history = ReadHistory();
            m_tracks = ReadTracks();

            m_client = new HttpClient();
            m_client.Timeout = new TimeSpan(m_configs.HttpRequestTimeoutMilliseconds * 10000);
            m_downloader = new Downloader(m_configs.TorrentPath, m_configs.ExecutablePath, m_configs.HttpRequestTimeoutMilliseconds);

            m_lastFetch = (DateTime.MinValue, "");

            m_isFetching = false;
            m_nextTrack = 0;
        }
        public void Dispose()
        {
            m_client.Dispose();
            m_downloader.Dispose();
        }

        public void Run()
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            //_ = FetchAll();
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
                            Console.WriteLine();
                            Console.WriteLine("help: show help");
                            Console.WriteLine("reload: reload tracks");
                            Console.WriteLine("fetchnext: fetch next track immediately");
                            Console.WriteLine("fetchall: fetch all tracks immediately");
                            Console.WriteLine("lastfetch: show info of last fetch");
                            Console.WriteLine("quit: quit anime tracker");
                            break;
                        case "reload":
                            m_tracks = ReadTracks();
                            m_nextTrack = 0;
                            break;
                        case "fetchnext":
                            if (m_isFetching)
                            {
                                Console.WriteLine("Fetching in progress. Please try again later.");
                            }
                            else
                            {
                                _ = FetchNext(true);
                            }
                            break;
                        case "fetchall":
                            if (m_isFetching)
                            {
                                Console.WriteLine("Fetching in progress. Please try again later.");
                            }
                            else
                            {
                                _ = FetchAll();
                            }
                            break;
                        case "lastfetch":
                            if (m_lastFetch.Item1 == DateTime.MinValue)
                            {
                                Console.WriteLine("No fetch history.");
                            }
                            else
                            {
                                Console.Write($"Last fetched at {m_lastFetch.Item1}, fetched: ");
                                if (m_lastFetch.Item2 == "")
                                {
                                    Console.WriteLine("all tracks");
                                }
                                else
                                {
                                    Console.WriteLine($"keywords: {m_lastFetch.Item2}");
                                }
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
                await Task.Delay(new TimeSpan(0, 0, m_configs.FetchPeriodSeconds), token);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!m_isFetching)
                {
                    await FetchNext();
                }
            }
        }

        async Task FetchNext(bool log = false)
        {
            if (m_tracks.Count == 0)
            {
                if (log)
                {
                    Console.WriteLine("No tracks found.");
                }
                return;
            }

            m_isFetching = true;
            List<string> hashList = new List<string>();
            Track track = m_tracks[m_nextTrack];
            if (log)
            {
                Console.WriteLine($"Fetching keywords: {track.Keywords}...");
            }
            string url = "https://mikanani.me/RSS/Search?searchstr=" + HttpUtility.UrlEncode(track.Keywords);
            List<Anime>? newAnime = await FetchAnimeList(url);
            if (newAnime == null)
            {
                if (m_configs.SkipFailures)
                {
                    m_nextTrack = (m_nextTrack + 1) % m_tracks.Count;
                }
            }
            else
            {
                foreach (Anime anime in newAnime)
                {
                    Console.WriteLine($"New anime: {anime.PublishDate}, {SizeToString(anime.Size, 1)}, {anime.Title}");

                    hashList.Add(anime.Hash);
                    m_history.Add(anime.Hash);
                    (bool success, string message) = await m_downloader.Download(anime.TorrentUrl, track.Path);
                    if (!success)
                    {
                        Console.WriteLine($"Download failed: {message}");
                    }
                }
                m_nextTrack = (m_nextTrack + 1) % m_tracks.Count;
            }

            WriteHistory(hashList);
            if (log)
            {
                Console.WriteLine($"Fetch complete. {hashList.Count} new anime found.");
                Console.WriteLine();
            }
            m_lastFetch = (DateTime.Now, track.Keywords);
            m_isFetching = false;
        }

        async Task FetchAll()
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
                List<Anime>? newAnime = await FetchAnimeList(url);
                if (newAnime == null)
                {
                    continue;
                }

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
            m_nextTrack = 0;
            m_lastFetch = (DateTime.Now, "");
            m_isFetching = false;
        }

        async Task<List<Anime>?> FetchAnimeList(string url)
        {
            List<Anime> list = new List<Anime>();
            Stream? content = null;
            for (int i = 0; i < m_configs.HttpRequestTries; i++)
            {
                try
                {
                    content = await m_client.GetStreamAsync(url);
                }
                catch(Exception) { }

                if (content != null)
                {
                    break;
                }
            }

            if (content == null)
            {
                Console.WriteLine($"Fetching url failed after {m_configs.HttpRequestTries} tries: {url}");
                return null;
            }

            using XmlReader reader = XmlReader.Create(content, new XmlReaderSettings { Async = true });

            SyndicationFeed feed = SyndicationFeed.Load(reader);

            foreach (SyndicationItem item in feed.Items)
            {
                if (TryReadAnime(item, out Anime anime))
                {
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

            return list;
        }

        static bool TryReadAnime(SyndicationItem item, out Anime anime)
        {
            MikanTorrent? torrent = null;
            foreach (SyndicationElementExtension extension in item.ElementExtensions)
            {
                try
                {
                    torrent = extension.GetObject<MikanTorrent>();
                }
                catch (Exception) { }

                if (torrent != null)
                {
                    break;
                }
            }

            if (torrent == null)
            {
                anime = default;
                return false;
            }

            string? torrentUrl = null;
            foreach (SyndicationLink link in item.Links)
            {
                if (link.RelationshipType == "enclosure")
                {
                    torrentUrl = link.Uri.AbsoluteUri;
                    break;
                }
            }

            if (torrentUrl == null)
            {
                anime = default;
                return false;
            }

            anime = new Anime()
            {
                Hash = Convert.ToHexString(SHA1.HashData(Encoding.Unicode.GetBytes(item.Id))),
                Title = item.Title.Text,
                PublishDate = torrent.PubDate,
                TorrentUrl = torrentUrl,
                Size = torrent.ContentLength
            };
            return true;
        }

        static Configs ReadConfigs()
        {
            Configs configs = new Configs
            {
                TorrentPath = "C:\\Users\\wjw11\\Downloads",
                ExecutablePath = "C:\\Program Files\\BitComet\\BitComet.exe",
                EarlyTerminate = true,
                FetchPeriodSeconds = 30,
                HttpRequestTimeoutMilliseconds = 10000,
                HttpRequestTries = 3,
                SkipFailures = false
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
                        else if (string.Compare(key, "fetchPeriodSeconds", true) == 0)
                        {
                            if (int.TryParse(value, out int p) && p > 0)
                            {
                                configs.FetchPeriodSeconds = p;
                            }
                        }
                        else if (string.Compare(key, "httpRequestTimeoutMilliseconds", true) == 0)
                        {
                            if (int.TryParse(value, out int t) && t > 0)
                            {
                                configs.HttpRequestTimeoutMilliseconds = t;
                            }
                        }
                        else if (string.Compare(key, "httpRequestTries", true) == 0)
                        {
                            if (int.TryParse(value, out int t) && t > 0)
                            {
                                configs.HttpRequestTries = t;
                            }
                        }
                        else if (string.Compare(key, "skipFailures", true) == 0)
                        {
                            if (bool.TryParse(value, out bool s))
                            {
                                configs.SkipFailures= s;
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
