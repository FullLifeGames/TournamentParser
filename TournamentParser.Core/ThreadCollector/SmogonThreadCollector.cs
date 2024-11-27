using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TournamentParser.Util;

namespace TournamentParser.ThreadCollector
{
    public class SmogonThreadCollector : IThreadCollector
    {
        public async Task<IDictionary<string, List<string>>> GetGeneralThreadsForForums(
            string filter, IDictionary<string, string>? additionals = null)
        {
            var tournamentToLinks = new Dictionary<string, string>();
            var smogonMain = await Common.HttpClient.GetStringAsync("http://www.smogon.com/forums/").ConfigureAwait(false);
            var scanStartOne = false;

            var lines = smogonMain.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (scanStartOne)
                {
                    if (line.Contains("class=\"subNodeLink subNodeLink--forum"))
                    {
                        var nextLine = lines[i + 1];
                        var tourName = nextLine[(nextLine.LastIndexOf(">") + 1)..];
                        if (tourName.Contains('<'))
                        {
                            tourName = tourName[..tourName.IndexOf("<")];
                        }

                        var tourUrl = line[(line.IndexOf(Common.Quotation) + 1)..];
                        tourUrl = tourUrl[..tourUrl.IndexOf(Common.Quotation)];
                        tourUrl = "http://www.smogon.com" + tourUrl;

                        if (!tournamentToLinks.ContainsKey(tourName))
                        {
                            tournamentToLinks.Add(tourName, tourUrl);
                        }
                    }
                    else if (line.Contains("node-extra\""))
                    {
                        scanStartOne = false;
                    }
                }
                else if (line.Contains($">{ filter }<"))
                {
                    scanStartOne = true;
                }
            }

            if (additionals != null)
            {
                foreach (var additional in additionals)
                {
                    if (!tournamentToLinks.ContainsKey(additional.Key))
                    {
                        tournamentToLinks.Add(additional.Key, additional.Value);
                    }
                }
            }

            var threadsForForums = new ConcurrentDictionary<string, List<string>>();
            await Parallel.ForEachAsync(tournamentToLinks, Common.ParallelOptions, async (kv, ct) =>
            {
                threadsForForums.AddOrUpdate(kv.Value, new List<string>(), (_, oldValue) => oldValue);
                var site = await Common.HttpClient.GetStringAsync(kv.Value, ct).ConfigureAwait(false);
                var pages = 1;
                if (site.Contains("<nav class=\"pageNavWrapper"))
                {
                    var temp = site;
                    while (temp.Contains("pageNav-page"))
                    {
                        temp = temp[(temp.IndexOf("pageNav-page") + "pageNav-page".Length)..];
                    }
                    temp = temp[(temp.IndexOf(">") + 1)..];
                    temp = temp[(temp.IndexOf(">") + 1)..];
                    temp = temp[..temp.IndexOf("<")];
                    pages = int.Parse(temp);
                }

                Console.WriteLine("Looking for scannable tournament threads in: " + kv.Value);
                var beforeCount = threadsForForums[kv.Value].Count;
                for (var pageCount = 1; pageCount <= pages; pageCount++)
                {
                    site = await Common.HttpClient.GetStringAsync(kv.Value + "page-" + pageCount, ct).ConfigureAwait(false);

                    foreach (var line in site.Split('\n'))
                    {
                        if (line.Contains("data-preview-url"))
                        {
                            var tempInside = line[(line.IndexOf("data-preview-url") + "data-preview-url".Length)..];
                            tempInside = tempInside[(tempInside.IndexOf(Common.Quotation) + 1)..];
                            if (!tempInside.Contains("/preview"))
                            {
                                continue;
                            }
                            tempInside = tempInside[..(tempInside.IndexOf("/preview") + 1)];
                            var url = "http://www.smogon.com" + tempInside;
                            threadsForForums[kv.Value].Add(url);
                        }
                    }
                }
                var afterCount = threadsForForums[kv.Value].Count;
                Console.WriteLine("Found " + (afterCount - beforeCount) + " scannable tournament threads in: " + kv.Value);
                Console.WriteLine();
            }
            ).ConfigureAwait(false);

            return threadsForForums;
        }

        public async Task<IDictionary<string, List<string>>> GetThreadsForForums()
        {
            return await GetGeneralThreadsForForums("Tournaments", new Dictionary<string, string>()
            {
                { "Standard Tournament Forums", Common.OfficialTournamentSite },
                { "PS! Tournaments", "https://www.smogon.com/forums/forums/ps-tournaments.698/" }
            }).ConfigureAwait(false);
        }

        public async Task<IDictionary<string, List<string>>> GetNonTourThreadsForForums()
        {
            var list = new Dictionary<string, List<string>>();
            foreach (var thread in new [] { "Smogon Metagames", "Ruins of Alph", "Battle Stadium" })
            {
                var additionalList = await GetGeneralThreadsForForums(thread).ConfigureAwait(false);
                additionalList.ToList().ForEach(x => list.Add(x.Key, x.Value));
            }
            return list;
        }
    }
}
