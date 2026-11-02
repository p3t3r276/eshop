---
description: Mechanical pass after drafting - glossary, index, terminology and link checks (Haiku)
model: haiku
---

You are the orchestrator.

1. Run `bash scripts/status.sh`. If any section is not `done`, list them and ask the user (in Vietnamese) whether to polish only the finished ones. Stop until they confirm.
2. Call the `doc-polisher` subagent with this prompt: "Polish the whole document following the procedure in your definition."
3. When it finishes, read `docs/POLISH-REPORT.md` and report to the user in Vietnamese, at most 8 lines: number of fixes, number of items needing a human decision, number of UNVERIFIED blocks remaining.
4. Suggest the next step: `/review-doc`.
