---
description: Fix the document from docs/REVIEW.md using doc-writer in fix mode (Sonnet)
argument-hint: [--all to include P2 items]
model: sonnet
---

You are the orchestrator. Do not edit content yourself; delegate to the `doc-writer` subagent in fix mode.

1. Read `docs/REVIEW.md`. Take the items that are still `- [ ]`, P0 first and then P1. Include P2 only if `$ARGUMENTS` contains `--all`.
2. Group the items by section ID. Handle at most 3 sections per wave, calling `doc-writer` in parallel in the same turn. Each prompt is: "Fix mode, section <ID>. Items to handle:" followed by exactly that section's lines copied verbatim from REVIEW.md.
3. When the writers return, change `- [ ]` to `- [x]` for each item the writer reports as handled. For items not handled, keep `- [ ]` and append `(not fixed: reason)` to the line.
4. If items remain for other sections, run the next wave, or stop and suggest `/clear` then `/fix-review` again. This command is safe to re-run because writers check whether each problem still exists before fixing it.
5. Report to the user in Vietnamese, at most 8 lines: items fixed, items remaining, sections changed.
6. When no P0 or P1 items remain, suggest `bash scripts/build.sh` to merge and export, or another `/review-doc` if the changes were large.
