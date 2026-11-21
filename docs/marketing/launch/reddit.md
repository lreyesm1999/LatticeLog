# Reddit drafts

Status: **drafts, not posted.** Three different angles for three different communities. Do not cross-post the same
text, and space the posts out (a week or more apart) so each one can stand on its own discussion.

General rules for all three:

- Read each subreddit's current rules and sidebar before posting; they change. Several communities ask that
  self-promotion stay a small share of your activity (the common "10 % rule"), require a flair, or only allow project
  posts on certain days or in a weekly thread.
- Post from an account with real participation in that community. Say in the first lines that you are the author.
- Lead with the technical content. The repository link goes at the end, once.
- Answer criticism on its merits. If someone points out a real gap, thank them and open an issue for it.
- No vote requests, no alternate accounts, no reposting deleted posts.

---

## 1. r/dotnet (alternative: r/csharp)

**Angle:** what it took to get a source-generated, typed logging call to 56 ns / 5 B on the enabled path, including the
steps that made things worse before they got better.

**Flair:** whatever the subreddit uses for projects or "Show and tell"; check the sidebar.

**Title:** `From 189 ns to 56 ns per typed log event: a bounded ring, a redaction fix and a record pool (with the numbers that got worse)`

**Body:**

> I'm the author of LatticeLog, an MIT-licensed logging engine for .NET 10. This post is about one part of it: the
> producer hot path for typed events, and what each change did to the measurements.
>
> Events are declared once:
>
> ```csharp
> [LatticeEvent(Id = 2101, Name = "inventory.stock.timeout", Level = Severity.Error,
>               Message = "Stock query for {productId} timed out after {elapsedMs} ms")]
> public static partial void StockTimeout(ILatticeLogger logger, long productId, int elapsedMs);
> ```
>
> A Roslyn source generator writes the method body, so there is no reflection or string formatting on the call, and
> `productId` is stored as an Int64, not as text.
>
> Measured with BenchmarkDotNet against a null sink (producer + queue + writer thread, no disk), one producer thread,
> one machine (i9-14900KF, Windows 11, .NET 10.0.15):
>
> | step | typed event (4 fields) | allocated |
> |---|---:|---:|
> | `ConcurrentQueue` + a `SortedSet` of tickets | 189 ns | 417 B |
> | preallocated bounded ring, position = ticket | 80 ns | 488 B |
> | redaction without enumerators | 75 ns | 360 B |
> | record pool | **56 ns** | **5 B** |
>
> Two things I didn't expect:
>
> 1. **The ring made allocations go *up*.** Before it, two thirds of the records were dropped by saturation and never
>    reached the writer thread, so their cost was never counted. With the ring, 0 of 193 M records were dropped and
>    the writer's real allocation showed up.
> 2. That exposed the next problem: the redaction step iterated an `IReadOnlyList` with `foreach`, one enumerator per
>    attribute per record. An index loop and a per-descriptor sensitivity cache fixed it.
>
> The pool only recycles a record when the sink declares it doesn't retain records; any other sink just leaves them to
> the GC. Caller-supplied attribute arrays are never reused.
>
> With the level disabled the call costs ~1.1 ns and 0 B. That is still slower than a disabled
> `LoggerMessage`-generated call in Microsoft.Extensions.Logging (~0.1 ns) because of an interface dispatch on
> `IsEnabled`; I have an idea to fix that but haven't done it.
>
> Caveats: null sink, single producer, one Windows machine (one whose CPU is unstable under all-core load, so I don't
> trust concurrency numbers from it). The cost with a real disk at fixed rates, measured with coordinated omission
> accounted for, is in a separate report.
>
> Full write-up with the raw runs: `reports/benchmarks-hotpath.md` and `reports/latency-v1.md` in
> https://github.com/lreyesm1999/LatticeLog
>
> If you already use `ILogger`, there's a provider (`builder.Logging.AddLatticeLog(...)`), but message-template logging
> through MEL is slower than the generated path, since MEL formats and allocates its state. Happy to hear where the
> benchmark setup is wrong.

---

## 2. r/programming (alternatives: r/compsci, r/databasedevelopment)

**Angle:** designing an open binary log format, keeping it honest with an independent reader, and publishing the
negative results.

**Title:** `Designing an open binary log format: an independent reader, golden files, and the experiments that didn't pay off`

**Body:**

> I've been building `.llog`, the on-disk format of LatticeLog (MIT, alpha). A few design notes and results,
> including the ones that went against me.
>
> **Layout.** Append-only segments made of blocks. Each block has a 24-byte header and a CRC. Keys, event names and
> message templates go into a catalog; record values are grouped by field and then DEFLATE-compressed (the codec is
> called Lattice-S). A sealed segment ends with a footer: a block directory, a bloom filter for trace ids, a copy of
> the catalog (added in 0.2 so that one flipped bit in a schema block no longer makes 27/27 records unreadable in the
> golden test), and, since 0.3, per-block summaries so severity/event/service filters can skip whole blocks.
>
> **Keeping the spec honest.** The spec is written as a normative document, and a second reader written
> independently in Python (no shared code) has to decode frozen golden files for every version, including two corrupt
> ones (a bit flip and a truncation), on every CI run. Existing golden files are never edited; a format change adds a
> new set.
>
> **Results, on one machine:**
>
> - Typed events from 14 Loghub-2.0 datasets: 25 % smaller in total than the same content as CSV + zstd-3, smaller on
>   all 14. But 17 % *larger* than CSV + zstd-19. And these start from Loghub's own annotated templates, which is the
>   best case.
> - Synthetic workloads: smaller than JSONL + zstd-3 on 7 of 8; zstd-19 is smaller on every one, at roughly 100× the
>   compression time.
> - Queries: a full `.llog` scan was 4–9× faster than decompress + parse + filter on JSONL + zstd; index pruning on
>   time/severity windows gave another 20–40×.
>
> **Negative results I kept:**
>
> - **Byte-exact raw text compresses badly.** Importing raw log lines so they can be exported byte for byte costs 83 %
>   more than the same text under zstd-3. Line bodies are opaque to the codec, and DEFLATE's 32 KiB window can't
>   compete. It's useful for keeping the exact original, not for saving space.
> - **A predictive codec (Lattice-P) wasn't built.** Before writing it, I estimated an optimistic bound with adaptive
>   code lengths (order-0 vs order-1 event identity, Δt vs ΔΔt timestamps). The best case was 0.6 % of the compressed
>   size, so the gate said no.
> - **Context inheritance (Lattice-C)** saves 3–21 % before general compression but only 0–3.5 % after DEFLATE. It's
>   an option, not the default.
> - The aspirational target of 30 % below the best baseline was not met.
>
> I'd like criticism of the format itself: anything underspecified, alignment or endianness choices, whether zstd as a
> block codec is worth a format bump, and what you'd want from the footer index.
>
> Spec: `specs/llog-format-v0.md`. Reports: `reports/baseline-v1.md`, `reports/loghub2-v1.md`.
> Repo: https://github.com/lreyesm1999/LatticeLog (I'm the author.)

**Notes:** r/programming removes posts that read as ads; keep the title about the design, not the product. If the post
is removed, don't repost it; ask the moderators.

---

## 3. r/ClaudeAI (alternatives: r/mcp, r/LocalLLaMA)

**Angle:** giving a coding agent your application's logs by service name over MCP, with an explicit coverage statement
and untrusted-content marking, and no model inside the tool.

**Title:** `Letting a coding agent read your local service logs by name over MCP, with "coverage" so it can tell "nothing found" from "didn't look"`

**Body:**

> I'm the author of LatticeLog, an MIT-licensed local log engine (.NET, with Python and PHP SDKs). One part of it is
> a read-only MCP server, and I'd like feedback from people who use agents for debugging.
>
> What bothered me about pointing an agent at log files:
>
> - it has to know paths and formats;
> - results get silently truncated, and the agent can't tell "no errors" from "only read the first 2,000 lines";
> - log lines are attacker-controllable text that ends up in the model's context.
>
> What the MCP server does instead:
>
> - `list_sources` finds services by name: .NET stores and Python services that write LatticeLog's format into a
>   registered folder, plus old text/JSON logs you explicitly converted.
> - `get_error_groups`, `search_events`, `get_trace`, `get_related_events`, `get_context_pack`, `get_overview`,
>   `get_event_schema`, `get_event`. Error groups are deterministic fingerprints, not root causes.
> - Every response has a `coverage` block: matched, returned and omitted counts, `truncated`, `partial_results`,
>   `sampling`, and `exhaustive: yes/no`. Context packs fit within a byte budget and say what they left out.
> - Every record has an `llog://` reference the agent can resolve with `get_event` to check a claim.
> - The output lists which fields contain text copied from logs, so the client can treat them as data.
> - Converting old logs (`preview_migration`, `migrate_logs`) only happens when asked; originals are never modified.
> - LatticeLog runs no model and performs no inference. It only structures, filters and retrieves.
>
> Setup is `latticelog mcp install --client claude-code` (also `claude-desktop`, `cursor`, `vscode`); it backs up the
> existing config and asks before replacing an entry. A per-user background service serves all clients from one
> process.
>
> Limitations: alpha, packages aren't on NuGet/PyPI yet (prebuilt executables for Windows x64 and Linux x64 are on
> GitHub Releases), and I haven't run a structured evaluation with external models yet, so I can't claim it makes an
> agent better at debugging. That's exactly what I'd like to hear about.
>
> Example flow, scripted end to end: `examples/developer-flow` in
> https://github.com/lreyesm1999/LatticeLog

**Notes:** r/ClaudeAI and r/LocalLLaMA have specific flairs for projects/tools; r/LocalLLaMA expects relevance to
local models, so if posting there, lead with "works with any MCP client, nothing leaves the machine" and don't make it
about one vendor.
