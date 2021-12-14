using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            var smogonMain = await Common.HttpClient.GetStringAsync("http://www.smogon.com/forums/");
            var scanStartOne = false;

            foreach (var line in smogonMain.Split('\n'))
            {
                if (scanStartOne)
                {
                    if (line.Contains("class=\"subNodeLink subNodeLink--forum"))
                    {
                        var tourName = line[(line.IndexOf(">") + 1)..];
                        tourName = tourName[..tourName.IndexOf("<")];

                        var tourUrl = line[(line.IndexOf(Common.Quotation) + 1)..];
                        tourUrl = tourUrl[..tourUrl.IndexOf(Common.Quotation)];
                        tourUrl = "http://www.smogon.com" + tourUrl;

                        tournamentToLinks.Add(tourName, tourUrl);
                    }
                    else if (line.Contains("node-stats\""))
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

            IDictionary<string, List<string>> threadsForForums = new ConcurrentDictionary<string, List<string>>();

            await Parallel.ForEachAsync(tournamentToLinks, async (kv, ct) =>
            {
                threadsForForums.Add(kv.Value, new List<string>());
                var site = await Common.HttpClient.GetStringAsync(kv.Value, ct);
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
                    site = await Common.HttpClient.GetStringAsync(kv.Value + "page-" + pageCount, ct);

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
            );

            return threadsForForums;
        }

        public async Task<IDictionary<string, List<string>>> GetThreadsForForums()
        {
            return await GetGeneralThreadsForForums("Tournaments", new Dictionary<string, string>()
            {
                { "Standard Tournament Forums", Common.OfficialTournamentSite }
            });
        }

        public async Task<IDictionary<string, List<string>>> GetNonTourThreadsForForums()
        {
            return await GetGeneralThreadsForForums("Smogon Metagames");
        }

    }
}
