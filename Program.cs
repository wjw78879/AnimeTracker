using System.Xml;
using System.ServiceModel.Syndication;
using System.Runtime.Serialization;

namespace UNISLAND.AnimeTracker
{
    

    static class Program
    {
        static void Main()
        {
            //await TestSyndication();
            AnimeTracker animeTracker = new AnimeTracker();
            animeTracker.Run();
        }

        static async Task TestSyndication()
        {
            string url = "https://mikanani.me/RSS/Search?searchstr=lycoris+recoil+1080+%E5%96%B5%E8%90%8C+%E7%AE%80%E4%BD%93";

            try
            {
                Stream content = await new HttpClient().GetStreamAsync(url);

                using XmlReader reader = XmlReader.Create(content);

                SyndicationFeed feed = SyndicationFeed.Load(reader);

                Console.WriteLine(feed.Title);
                Console.WriteLine(feed.Items.Count());

                foreach (SyndicationItem item in feed.Items)
                {
                    Console.WriteLine($"{item.Id}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fetching url \"{url}\" failed: {ex.Message}");
            }
        }
    }
}