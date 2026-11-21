# Launch materials

Drafts for announcing LatticeLog publicly. **Nothing here has been posted.** Each piece is to be reviewed, adapted and
published by the maintainer, from their own accounts, after the checklist below is green.

| file | what it is |
|---|---|
| [`show-hn.md`](show-hn.md) | Show HN title options, post body, and prepared answers to likely questions |
| [`reddit.md`](reddit.md) | Three distinct technical posts (r/dotnet, r/programming, r/ClaudeAI) with community notes |
| [`devto-building-a-local-first-logging-engine.md`](devto-building-a-local-first-logging-engine.md) | Long-form DEV article: architecture, decisions, results, negative results, limitations |
| [`linkedin-x.md`](linkedin-x.md) | Three LinkedIn posts and five X posts, each tied to one feature and one linked source |
| [`integration-strategy.md`](integration-strategy.md) | How to find OSS projects where LatticeLog genuinely helps, and how to approach them |

## Recommended sequencing

1. **Fix the blockers** in the checklist below, at least the ones marked *before any launch*.
2. **Soft launch** (week 1–2): share the repository with a few .NET developers and MCP users you know personally. Ask
   them to run the five-minute demo on a clean machine and to write down every point where they got stuck. Fix those.
3. **DEV article** (week 3): publish the long-form article first. It carries the full context and the caveats, so later
   short posts can link to it.
4. **One Reddit post** (week 3–4): the r/dotnet angle first, since it's the closest audience. Wait at least a week before
   the next angle, and only post the next one if the first led to useful discussion.
5. **Show HN** (week 4+): once the demo has been run by people other than the maintainer, issues from the soft launch
   are answered, and the maintainer can stay in the thread for several hours.
6. **LinkedIn / X**: alongside each of the above, linking to it. Not on their own.
7. **Integrations with other projects**: only after the readiness gates in
   [`integration-strategy.md`](integration-strategy.md) are met. That is months, not weeks.

Measure each step by what matters: issues opened by people who tried it, questions about the design, contributors. Stars
are a weak signal.

## Pre-launch checklist

### Before any launch

- [ ] **Social preview image set** in the repository settings (Settings → General → Social preview). The repository
      currently uses GitHub's generated image.
- [ ] **README Quick Start re-run on a clean machine** (Windows and Linux), from clone to first query, by someone who
      hasn't built LatticeLog before.
- [ ] **GitHub Discussions enabled**, or a clear statement of where questions go. Discussions are currently disabled,
      and the issue templates don't allow blank issues, so a launch would leave people with questions nowhere to ask.
- [ ] **Every link in these drafts resolves on `main`** (several point to `docs/guides/`, which is new).
- [ ] **Numbers re-checked** against `reports/` on the day of posting.
- [ ] **Version wording consistent**: the drafts say `0.1.0-alpha`; the latest GitHub pre-release is
      `v0.1.0-alpha.7`.

### Known blockers to state openly (not to hide)

- **No packages** on NuGet, PyPI or Packagist. Users build from source or download the prebuilt Native AOT executables
  (Windows x64, Linux x64) from GitHub Releases. This is the biggest adoption barrier for library users and blocks the
  integration strategy entirely.
- **P14 (local v1.0) is NOT PASSED** in [`ROADMAP.md`](../../../ROADMAP.md): latency on another disk and on Linux,
  signing, N-1 compatibility and a release candidate are open.
- **All measurements come from one machine**, whose CPU is unstable under all-core load. Concurrency runs were pinned to
  4 cores. Repeating the benchmarks on stable hardware, ideally Linux, would make every quoted number stronger.
- **Single maintainer.** Be clear about response expectations; don't promise support levels that aren't established.
- **No encryption at rest, no external security review**, no cluster.
- **No structured evaluation with external AI models** yet, so no claim that MCP access improves agent debugging.

### Wording rules for every post

- No "blazing fast", "revolutionary", "next-generation" or similar.
- Every number links to the report it comes from, with its hardware caveat nearby.
- Say what LatticeLog is not: not a hosted observability platform, not a cluster, not production-hardened, and it runs
  no AI.
- Disclose authorship in every community post.
- No invented users, quotes, adoption figures or testimonials.
