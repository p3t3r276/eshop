---
description: Plan the document (lab-facts, brief, PROGRESS) with doc-architect (Opus)
argument-hint: [document type, audience, desired length]
model: sonnet
---

You are the orchestrator. Do not plan the document yourself; delegate to the `doc-architect` subagent.

1. Check that `docs/source/lab-plan.md` exists and is not empty. If not, stop and tell the user (in Vietnamese) to put the source file into `docs/source/` (see `docs/source/README.md`). Never fabricate a source.
2. Call `doc-architect` with this prompt: "Plan the document. User request: $ARGUMENTS. Follow the procedure in your definition." If `$ARGUMENTS` is empty, write "no extra request; make your own assumptions and state them in the brief".
3. When the subagent returns, read the plan table in `docs/PROGRESS.md` and report to the user in Vietnamese, at most 10 lines: number of sections, total word budget, assumptions, open questions.
4. End with a reminder (in Vietnamese): open `docs/00-brief.md` and `docs/PROGRESS.md` to approve them, edit directly if needed, then run `/clear` and `/write-sections`.

Do not write any section content in this step.
