# Tournament Parser

This tool goes through all Smogon Tournament threads and parses the match and user information, to be used here: <https://fulllifegames.com/Tools/MatchChecker/>

More description: <https://www.smogon.com/forums/threads/tournament-match-checker.3628158/>

## Example

Visit: <https://fulllifegames.com/Tools/MatchChecker/?user=fulllifegames>

## Usage

Compile and use the command line application of the `TournamentParser.Cmd` project.

```
TournamentParser.Cmd [--full] [--delay <seconds>] [--recent-days <days>] [--max-requests <count>]
```

It scans the forums Tournaments, PS! Tournaments, Smogon Metagames, Ruins of Alph and Battle Stadium Singles, each with its subforums, and writes `output.txt`, `output.json` and `users/*.json`.

The crawl state is kept in `SmogonTournamentParser.db` in the working directory. Keep that file between runs:

- The first run reads every listing page and every thread. A `SmogonTournamentParser.db` from version 2.x is taken over without changing its entries: threads whose stored pages still cover the latest post are not read again, and pages that hold no content (2.x also stored bot checks) are read again. Back the file up before the first run of version 3.
- Later runs only read what changed. A forum whose latest post is the same as in the last run is not requested, nor are its subforums. A forum listing is read up to the first thread without new posts. Of a thread with a new post, a lock or a new title, page 1 (the tournament post with the pairings) and the pages from the last known page on are read.
- Forums and threads with posts younger than `--recent-days` (default 7) are looked at in every run, so that locks and title changes ("won by ...") are noticed even without a new post. Edits of older posts without any of these signals keep the state of their last read; `--full` reads everything again.
- `--max-requests` spreads a long first run over several runs: the run stops after that many requests, and the next one continues from there.

The output is always built from all stored threads, so it stays complete when a run reads only a few pages.

Requests go out one at a time with a pause of `--delay` seconds (default 2). The parser identifies itself with the User-Agent `TournamentParser/3.0 (+https://github.com/FullLifeGames/TournamentParser)`, waits as long as `Retry-After` asks and does not retry 404s. At the end it prints how many requests it sent.

If Smogon answers with a bot check or an unexpected page, the run stops with exit code 2; a reached `--max-requests` stops it with exit code 3. In both cases the output files are left as they were; threads read before the stop are kept and the next run continues from there.

### Tests

`dotnet test` runs offline against generated forum pages and saved Smogon pages. The tests that send requests to smogon.com are explicit; run them on purpose with `dotnet test --filter TestCategory=Live`.

## Package

The code can be used as a Nuget package, which can be found [here](https://www.nuget.org/packages/TournamentParser.Core/).

### Package Usage Examples

See how the tests use it [here](https://github.com/FullLifeGames/TournamentParser/blob/master/TournamentParser.Tests/Program.cs).
