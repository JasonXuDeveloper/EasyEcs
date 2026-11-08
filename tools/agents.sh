#!/usr/bin/env bash
# The agent instructions stay indexed and complete:
#  - the root AGENTS.md lists every directory AGENTS.md (its "> Read when:" line) and every skill (its description's
#    "Use when ..." part), between the agents-index / skills-index markers;
#  - each directory AGENTS.md has a CLAUDE.md next to it that is the one line @AGENTS.md (Claude Code imports it);
#  - every source file of src/, tests/ and benchmarks/ is named in its directory's file map, by its path in its project,
#    and a map line naming a file that does not exist fails too.
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

# A project's file map names each of its source files by its path in the project (Locality/Chase.cs), at any depth:
# the tracked and untracked files, so obj/ and bin/ never count, and a file moved into a folder must be named there.
maps = {"src/EasyEcs.Core": "src/EasyEcs.Core/AGENTS.md", "src/EasyEcs.Generator": "src/EasyEcs.Generator/AGENTS.md",
        "src/EasyEcs.CodeFixes": "AGENTS.md"}
for parent in ("tests", "benchmarks"):
    for project in sorted({os.path.dirname(f) for f in files if f.startswith(parent + "/") and f.endswith(".csproj")}):
        maps[project] = f"{parent}/AGENTS.md"
for d, m in maps.items():
    text = open(m).read()
    for f in sorted(f for f in files if f.startswith(d + "/") and f.endswith(".cs") and os.path.isfile(f)):
        name = os.path.relpath(f, d)
        if not re.search(rf"(?<![\w./-]){re.escape(name)}(?![\w])", text):
            problems.append(f"{f} is not in the file map {m} (as {name})")

# The other direction: a map line that names a file of the project (Folder/File.cs, or an entry at the start of a line)
# must name one that exists, so a rename or a delete cannot leave a stale line behind.
known = {}
for d, m in maps.items():
    known.setdefault(m, []).append(d)
for m, projects in known.items():
    text = open(m).read()
    have = {os.path.relpath(f, d) for d in projects for f in files if f.startswith(d + "/") and f.endswith(".cs")}
    names = set()
    for token in re.findall(r"(?<![\w./-])((?:[A-Z]\w*/)+[\w.]+\.cs)(?![\w])", text):
        # only paths that start in a folder of one of this map's projects (others name another project's file)
        if any(os.path.isdir(os.path.join(d, token.split("/")[0])) for d in projects): names.add(token)
    for token in re.findall(r"^ {2}([\w.]+(?:/[\w.]+)*\.cs)(?=\s|$)", text, re.M): names.add(token)
    for name in sorted(names - have):
        problems.append(f"{m} names {name}, which is not a file of its projects")

for p in problems: print(p)
print(f"agents: {len(problems)} problems")
sys.exit(1 if problems else 0)
PY
