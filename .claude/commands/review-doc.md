---
description: Independent review of the whole document with doc-reviewer (Opus); output goes to docs/REVIEW.md
model: sonnet
---

You are the orchestrator. Do not review yourself; delegate to the `doc-reviewer` subagent.

1. Run `bash scripts/status.sh`. If any section is not `done`, list them and ask the user (in Vietnamese) whether to review only the finished ones. Stop until they confirm.
2. Call `doc-reviewer` with this prompt: "Review the whole document following the procedure in your definition." Add no commentary or context about how the document was written, so the reviewer keeps an independent view.
3. When it finishes, read the "Summary" part of `docs/REVIEW.md` and report to the user in Vietnamese, at most 8 lines: item counts by P0/P1/P2 and the heaviest section.
4. Remind the user (in Vietnamese) to read `docs/REVIEW.md` before fixing, to delete any line they disagree with, and then to run `/fix-review`.
