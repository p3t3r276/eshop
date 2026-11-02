# PROGRESS: document plan

> Filled in by `doc-architect` when you run `/plan-doc`. The plan table can be edited by hand (add, remove or reorder sections, change budgets) before running `/write-sections`.
>
> Section status is NOT stored here. It lives in the `status:` field of each section file's front matter. View it with `bash scripts/status.sh`. This way, writers running in parallel never overwrite each other.

## Plan table

| ID | File | Source (heading in lab-plan.md) | Budget (words) | Depends on |
|----|------|---------------------------------|----------------|------------|
| S01 | sections/01-example.md | (example) Lab goals and scope | 1500 | - |
| S02 | sections/02-example.md | (example) Technology stack | 1800 | S01 |

<!-- The two example rows above are replaced when /plan-doc runs. Keep exactly 5 columns. "Depends on" is a comma-separated list of IDs, or "-". -->

## Open questions (written by doc-architect, answered by the user)

(none yet)

## Gaps in the sources

Summarized from `docs/gaps/*.md` after each writing wave. Look at them directly with `ls docs/gaps` and read each file.
