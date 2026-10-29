#!/usr/bin/env python3
"""Sinh lại shoplab.slnx từ mọi *.csproj trong src/ và tests/.
Tương đương chạy `dotnet sln add` cho từng project, dùng khi thêm service mới.
Chạy: python3 tools/sln-sync.py
"""
from pathlib import Path

root = Path(__file__).resolve().parent.parent
folders = {}
for proj in sorted(root.glob("src/**/*.csproj")) + sorted(root.glob("tests/**/*.csproj")):
    rel = proj.relative_to(root).as_posix()
    if "/node_modules/" in rel:
        continue
    parts = rel.split("/")
    if parts[0] == "tests":
        folder = "/tests/"
    elif parts[1] in ("Services", "Gateways"):
        folder = f"/src/{parts[1]}/"
    else:
        folder = "/src/"
    folders.setdefault(folder, []).append(rel)

lines = ["<Solution>"]
for folder in sorted(folders):
    lines.append(f'  <Folder Name="{folder}">')
    for rel in folders[folder]:
        lines.append(f'    <Project Path="{rel}" />')
    lines.append("  </Folder>")
lines.append("</Solution>")
(root / "shoplab.slnx").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"Đã ghi {sum(len(v) for v in folders.values())} project vào shoplab.slnx")
