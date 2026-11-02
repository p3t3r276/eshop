---
description: Write the sections that are ready (up to 3 in parallel) with doc-writer (Sonnet)
argument-hint: [S01 S02 ... | empty = next wave]
model: sonnet
---

You are the orchestrator. Do NOT write section content yourself; delegate to the `doc-writer` subagent.

1. Run `bash scripts/status.sh ready` to get the sections that are ready (dependencies satisfied; unfinished `draft` sections are listed first).
2. If `$ARGUMENTS` contains specific IDs, use only those. Still check them against the ready list: report and skip any ID whose dependencies are not satisfied.
3. Pick at most 3 sections. If the list is empty, run `bash scripts/status.sh`, report the situation and stop.
4. Call `doc-writer` once per chosen section, all in the same turn so they run in parallel. Each prompt is only: "Write section <ID>. Follow the procedure in your definition." Do not paste brief or source content into the prompt.
5. When all have returned, run `bash scripts/status.sh` and list the files in `docs/gaps/` if any.
6. Report to the user in Vietnamese, at most 8 lines: sections finished, sections remaining, number of UNVERIFIED blocks and gaps, next step. If sections remain, suggest `/clear` then `/write-sections`. If none remain, suggest `/polish-doc`.

Do not re-read the section files that were just written. Trust the writers' reports and `status.sh`, so this session's context stays small.
