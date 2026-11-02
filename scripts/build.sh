#!/usr/bin/env bash
# Merge docs/sections/*.md into docs/dist/doc.md, then export docx/pdf if pandoc is available.
set -euo pipefail
cd "$(dirname "$0")/.."
shopt -s nullglob

files=(docs/sections/*.md)
[ ${#files[@]} -gt 0 ] || { echo "No sections yet." >&2; exit 1; }

notdone=$(bash scripts/status.sh | awk 'NR>1 && $1 ~ /^S[0-9]+$|^\?$/ && $2 != "done" {print $1" ("$2")"}')
if [ -n "$notdone" ]; then
  echo "Warning: some sections are not done yet:"
  echo "$notdone"
fi

mkdir -p docs/dist
out=docs/dist/doc.md
: > "$out"
for f in $(printf '%s\n' "${files[@]}" | sort); do
  # strip the front matter at the top of each file
  awk 'NR==1 && /^---$/ {fm=1; next} fm && /^---$/ {fm=0; next} !fm {print}' "$f" >> "$out"
  printf '\n\n' >> "$out"
done
echo "Merged: $out ($(wc -w < "$out") words)"

if command -v pandoc >/dev/null 2>&1; then
  pandoc "$out" --toc -o docs/dist/doc.docx && echo "Exported: docs/dist/doc.docx"
  if pandoc "$out" --toc -o docs/dist/doc.pdf 2>/dev/null; then
    echo "Exported: docs/dist/doc.pdf"
  else
    echo "Could not export PDF (usually a missing LaTeX engine). Use the docx, or give doc.md to Claude to export."
  fi
else
  echo "pandoc is not installed, so only docs/dist/doc.md was produced. Install pandoc to export docx/pdf."
fi
