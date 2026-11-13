# Tournament Parser

This tool goes through all Smogon Tournament threads and parses the match and user information, to be used here: <https://fulllifegames.com/Tools/MatchChecker/>

More description: <https://www.smogon.com/forums/threads/tournament-match-checker.3628158/>

## Example

Visit: <https://fulllifegames.com/Tools/MatchChecker/?user=fulllifegames>

## Usage

Compile and use the command line application of the `TournamentParser.Cmd` project.

```
TournamentParser.Cmd [--full] [--no-search] [--delay <seconds>] [--recent-days <days>] [--max-requests <count>] [--headed]
```

It scans the forums Tournaments, PS! Tournaments, Smogon Metagames, Ruins of Alph and Battle Stadium Singles, each with its subforums, and writes `output.txt`, `output.json` and `users/*.json`.

The crawl state is kept in `SmogonTournamentParser.db` in the working directory. Keep that file between runs:

- The first run reads every listing page and every thread. A `SmogonTournamentParser.db` from version 2.x is taken over without changing its entries: threads whose stored pages still cover the latest post are not read again, and pages that hold no content (2.x also stored bot checks) are read again. Back the file up before the first run of version 3.
- Later runs only read what changed. A forum whose latest post is the same as in the last run is not requested, nor are its subforums. A forum listing is read up to the first thread without new posts.
- Only threads with pairings in posts 1 or 2, and replay threads (`-replay` in the URL), can yield matches after page 1: a replay post only counts if its author is paired in the thread. Of all other threads only page 1 is read. Posters without matches in those threads no longer show up in the output; the matches stay the same.
- Each run first asks Smogon's search for the posts with replay links (`replay.pokemonshowdown.com`) in the changed forums since the last run. A thread with new posts is read only when the search finds a new replay post in it, or when its title or lock changes: page 1 (the tournament post, where hosts add pairings and bold winners), and of a tournament thread the new pages with replay posts. Winners bolded without a replay post show up at the next replay post or at the lock. Replay threads are read on every new post. A tournament thread with more than five pages to read that the search of the run does not cover (a new thread, for example) first searches for its own replay posts, unless they are spread over so many pages that reading them is cheaper. `--no-search` turns the search off: then the pages from the last known page on are read.
- Forums with an unlocked tournament thread (pairings in posts 1 or 2, or a replay thread) with posts younger than `--recent-days` (default 7) are listed in every run, so that locks and title changes ("won by ...") are noticed even without a new post. Edits of older posts without any of these signals keep the state of their last read; `--full` reads everything again.
- `--max-requests` spreads a long first run over several runs: the run stops after that many requests, and the next one continues from there.

The output is always built from all stored threads, so it stays complete when a run reads only a few pages.

Requests go out one at a time with a pause of `--delay` seconds (default 2). The parser identifies itself with the User-Agent `TournamentParser/3.0 (+https://github.com/FullLifeGames/TournamentParser)`, waits as long as `Retry-After` asks and does not retry 404s. At the end it prints how many requests it sent.

Smogon answers thread pages with a JavaScript bot check (a proof-of-work that sets a cookie for 7 days). The parser sends its requests as plain HTTP; when the bot check appears, that page is loaded once in a headless Chromium through [Playwright](https://playwright.dev/dotnet/), which passes the check, and the following requests carry the cookie it got, with the same User-Agent. If Smogon stops accepting the cookie for plain requests, those pages are loaded in the browser for the rest of the run. The browser loads nothing but the page itself: no scripts, stylesheets, images, ads or frames. Chromium is downloaded on first use (to `%LOCALAPPDATA%\ms-playwright` or `~/.cache/ms-playwright`). On Linux, the system libraries Chromium needs are installed once with `pwsh bin/<config>/net8.0/playwright.ps1 install-deps chromium` (or see [the Playwright docs](https://playwright.dev/dotnet/docs/browsers#install-system-dependencies)). `--headed` shows the browser window.

If Smogon answers with a bot check or an unexpected page, the run stops with exit code 2; a reached `--max-requests` stops it with exit code 3. In both cases the output files are left as they were; threads read before the stop are kept and the next run continues from there.

### Tests

`dotnet test` runs offline against generated forum pages and saved Smogon pages. The tests that send requests to smogon.com are explicit; run them on purpose with `dotnet test --filter TestCategory=Live`. The tests of the browser handler run Chromium against a local server: `dotnet test --filter TestCategory=Browser`.

## Package

The code can be used as a Nuget package, which can be found [here](https://www.nuget.org/packages/TournamentParser.Core/).

### Package Usage Examples

See how the tests use it [here](https://github.com/FullLifeGames/TournamentParser/blob/master/TournamentParser.Tests/Program.cs).
