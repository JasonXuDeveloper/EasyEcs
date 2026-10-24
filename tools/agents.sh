#!/usr/bin/env bash
# The agent instructions stay indexed and complete:
#  - the root AGENTS.md lists every directory AGENTS.md (its "> Read when:" line) and every skill (its description's
#    "Use when ..." part), between the agents-index / skills-index markers;
#  - each directory AGENTS.md has a CLAUDE.md next to it that is the one line @AGENTS.md (Claude Code imports it);
#  - every source file of src/ is named in its directory's file map.
# tools/agents.sh writes the indexes; tools/agents.sh --check fails when anything is stale or missing (CI, verify).
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "${1:-}" <<'PY'
import glob, os, re, subprocess, sys
check = sys.argv[1] == "--check"
problems = []

files = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard"], text=True).split()
dirs = sorted(f for f in files if f.endswith("/AGENTS.md"))
for f in dirs + ["AGENTS.md"]:
    claude = os.path.join(os.path.dirname(f), "CLAUDE.md")
    if not os.path.isfile(claude) or open(claude).read().strip() != "@AGENTS.md":
        problems.append(f"{claude} must be the one line @AGENTS.md")

rows = ["| Directory | Read its AGENTS.md when |", "|---|---|"]
for f in dirs:
    m = re.search(r"^> Read when: (.+)$", open(f).read(), re.M)
    if not m:
        problems.append(f"{f} has no '> Read when:' line")
        continue
    rows.append(f"| [`{os.path.dirname(f)}/`]({f}) | {m.group(1).rstrip('.')} |")
agents = "\n".join(rows)

rows = ["| Skill | What it does | Use it |", "|---|---|---|"]
for f in sorted(glob.glob(".claude/skills/*/SKILL.md")):
    text = open(f).read()
    name = re.search(r"^name: (.+)$", text, re.M).group(1).strip()
    desc = re.search(r"^description: (.+)$", text, re.M).group(1).strip().strip('"').replace('\\"', '"')
    m = re.search(r"\b(Use (?:when|for|in|once|before)\b.*?)(?: Triggers:|$)", desc)
    if not m:
        problems.append(f"{f}: the description has no 'Use when ...' sentence")
        continue
    what = re.split(r" - |\. ", desc, maxsplit=1)[0].rstrip(".")
    use = re.sub(r"^Use ", "", m.group(1)).rstrip(".")
    rows.append(f"| [`{name}`]({f}) | {what} | {use} |")
skills = "\n".join(rows)

root = open("AGENTS.md").read()
def put(text, key, body):
    pat = re.compile(rf"(<!-- {key}:start -->\n).*?(<!-- {key}:end -->)", re.S)
    if not pat.search(text):
        problems.append(f"AGENTS.md has no {key} markers")
        return text
    return pat.sub(lambda m: m.group(1) + body + "\n" + m.group(2), text)
new = put(put(root, "agents-index", agents), "skills-index", skills)
if new != root:
    if check: problems.append("AGENTS.md indexes are stale: run tools/agents.sh")
    else: open("AGENTS.md", "w").write(new)

maps = {"src/EasyEcs.Core": "src/EasyEcs.Core/AGENTS.md", "src/EasyEcs.Generator": "src/EasyEcs.Generator/AGENTS.md",
        "src/EasyEcs.CodeFixes": "AGENTS.md"}
for d, m in maps.items():
    text = open(m).read()
    for f in sorted(glob.glob(f"{d}/*.cs")):
        if os.path.basename(f) not in text:
            problems.append(f"{f} is not in the file map {m}")

for p in problems: print(p)
print(f"agents: {len(problems)} problems")
sys.exit(1 if problems else 0)
PY
