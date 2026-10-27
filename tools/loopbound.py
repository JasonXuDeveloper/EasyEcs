#!/usr/bin/env python3
"""Where each vector loop of a Tier1 listing is bound: the ports, the front end or a loop-carried chain (llvm-mca).

    tools/loopbound.py <listing.asm> [--reg zmm] [--cpus icelake-server,znver4] [--keep DIR] [--all] [file.s ...]

Reads a DOTNET_JitStdOutFile listing (tools/loopbound.sh writes one), keeps the Tier1 / FullOpts methods, finds every
innermost loop (a back edge to an earlier label with no back edge inside) that uses the register class --reg, drops
copies of a loop already seen in the same job (a runner inlines a job's Execute at several sites), and runs llvm-mca
on each body. Prints a markdown row per job and loop kind ("whole": over whole vectors; "masked": a 64-row word's
loop, which tests its pass bits): instructions, then per CPU the simulated cycles per iteration | ports / front end /
chain | the bound. --all prints every loop (calls included, which the rows skip: cold rows and helper calls). Given
.s files instead of a listing (llvm-mca input, Intel syntax), it prints the same numbers for each, to compare variants.

The three bounds: ports = llvm-mca's block reciprocal throughput; front end = uops per iteration / the model's dispatch
width; chain = the longest path, with llvm-mca's latencies, from a register the loop reads before it writes it back
to that register (llvm-mca's "data dependencies" also counts long paths inside one iteration, which the out-of-order
window overlaps across iterations, so it is not used). Memory is not followed: each iteration touches other rows.
"""
import argparse, os, re, subprocess, sys, tempfile

# ---------------- registers read and written by an Intel-syntax instruction ----------------

GPR = {}
for n in ["ax", "bx", "cx", "dx", "si", "di", "bp", "sp"]:
    for v in ["r" + n, "e" + n, n, n[0] + "l" if n[1] == "x" else n + "l"]:
        GPR[v] = "r" + n
for i in range(8, 16):
    for v in [f"r{i}", f"r{i}d", f"r{i}w", f"r{i}b"]:
        GPR[v] = f"r{i}"

NO_DESTINATION = {"cmp", "test", "kortestw", "kortestb", "kortestq", "kortestd", "ktestw", "ktestb", "vptest", "ptest",
                  "bt", "vucomiss", "vucomisd", "vcomiss", "vcomisd", "prefetcht0", "prefetchnta"}
READS_DESTINATION = ("vfmadd", "vfmsub", "vfnmadd", "vfnmsub", "vpternlog", "vpermt2", "vpermi2", "vpdpbusd")
WRITES_ONLY = {"mov", "movzx", "movsx", "movsxd", "lea", "vmovups", "vmovaps", "vmovdqu", "vmovdqu32", "vmovdqu64",
               "vmovdqa", "vmovdqa32", "vmovdqa64", "vbroadcastss", "vbroadcastsd", "vpbroadcastd", "vpbroadcastq",
               "vpbroadcastb", "vpbroadcastw", "kmovw", "kmovb", "kmovq", "kmovd", "vpmovm2d", "vpmovm2q", "vpmovm2b",
               "vpmovm2w", "setne", "sete", "setl", "setg", "vmovd", "vmovq", "shrx", "shlx", "sarx", "vmovss",
               "vmovsd", "andn"}


def canon(token):
    token = token.lower()
    m = re.fullmatch(r"[xyz]mm(\d+)", token)
    if m:
        return "v" + m.group(1)
    if re.fullmatch(r"k[0-7]", token):
        return token
    return GPR.get(token)


def registers(operand):
    return [r for r in (canon(t) for t in re.findall(r"[a-z][a-z0-9]*", operand.lower())) if r]


def dependences(line):
    """(written, read) registers; a memory destination writes none, a merge-masked destination is read too."""
    parts = line.split(None, 1)
    op = parts[0].lower()
    operands = [o.strip() for o in re.split(r",(?![^\[]*\])", parts[1])] if len(parts) > 1 else []
    if not operands:
        return [], []
    first = operands[0]
    merge = "{k" in first and "{z}" not in first
    target = re.sub(r"\{.*?\}", "", first)
    read = [r for o in operands[1:] for r in registers(o)] + registers(" ".join(re.findall(r"\{(k[0-7])\}", first)))
    if "ptr" in target:
        return [], read + registers(target)
    written = registers(target)
    if op in NO_DESTINATION or op.startswith("j"):
        return [], read + written
    if op in ("xor", "vxorps", "vpxor", "vpxord", "vpxorq") and len(set(registers(" ".join(operands)))) == 1:
        return written, []  # a zero idiom
    if op in WRITES_ONLY or len(operands) >= 3 and not op.startswith(READS_DESTINATION):
        return written, read + (written if merge else [])
    return written, read + written


def mca(path, cpu, *options):
    return subprocess.run(["llvm-mca", f"-mcpu={cpu}", *options, path], capture_output=True, text=True)


def chain(path, cpu, instructions):
    """The longest loop-carried recurrence in cycles, and its register."""
    out = mca(path, cpu, "-iterations=1", "-instruction-info", "-resource-pressure=false", "-timeline=false").stdout
    latencies, on = [], False
    for line in out.split("\n"):
        if "Instructions:" in line and "[1]" in line:
            on = True
            continue
        if on:
            m = re.match(r"\s*(\d+)\s+(\d+)\s+", line)
            if not m:
                break
            latencies.append(int(m.group(2)))
    if len(latencies) != len(instructions):
        return 0, None
    deps = [dependences(i) for i in instructions]
    written, carried = set(), set()
    for w, r in deps:
        carried.update(x for x in r if x not in written)
        written.update(w)
    best, register = 0, None
    for seed in carried & written:
        depth = {seed: 0}
        for (w, r), latency in zip(deps, latencies):
            inputs = [depth[x] for x in r if x in depth]
            for x in w:
                if inputs:
                    depth[x] = max(inputs) + latency
                else:
                    depth.pop(x, None)
        if depth.get(seed, 0) > best:
            best, register = depth[seed], seed
    return best, register


def bound(path, cpu):
    """(cycles per iteration, ports, front end, chain, chain register, bound) of the loop in path."""
    out = mca(path, cpu, "-iterations=200").stdout
    cycles = re.search(r"Total Cycles:\s+(\d+)", out)
    if not cycles:
        return None
    per = int(cycles.group(1)) / 200
    uops = int(re.search(r"Total uOps:\s+(\d+)", out).group(1)) / 200
    ports = float(re.search(r"Block RThroughput:\s+([\d.]+)", out).group(1))
    front = uops / int(re.search(r"Dispatch Width:\s+(\d+)", out).group(1))
    instructions = [l.strip() for l in open(path) if l.strip() and not l.startswith(".") and not l.strip().endswith(":")]
    carried, register = chain(path, cpu, instructions)
    kind = max([("ports", ports), ("front end", front), ("chain", carried)], key=lambda t: t[1])[0]
    return per, ports, front, carried, register, kind


# ---------------- loops of a listing ----------------

LABEL = re.compile(r"^(G_M\d+_IG\d+):")
JUMP = re.compile(r"^\s+(j\w+)\s+(?:SHORT\s+)?(G_M\d+_IG\d+)")


def clean(line):
    """A listing line as llvm-mca input: no comments, GC pointer sizes as qword, data labels as a rip address."""
    line = line.split(";")[0].rstrip()
    if not line.strip() or line.strip().startswith("align"):
        return None
    line = re.sub(r"\b[gb]word ptr", "qword ptr", line)
    line = re.sub(r"\[reloc @RWD\d+(\+0x[0-9A-F]+)?\]", "[rip+0x1000]", line)
    line = re.sub(r"\]\s+\{1to(\d+)\}", r"]{1to\1}", line).replace("SHORT ", "")
    return re.sub(r"\s+", " ", line.strip())


def job(header):
    m = re.search(r"\+([A-Za-z0-9_]+(?:Job\d+\w*|Fusion\w*))[\]:]", header)
    return m.group(1) if m else re.sub(r"\(.*", "", header)[-60:]


def loops(listing, register):
    """(job, kind, body lines, calls) of every innermost loop using the register class, one per distinct body."""
    text = open(listing, encoding="utf-8", errors="replace").read()
    for method in re.split(r"^; Assembly listing for method ", text, flags=re.M)[1:]:
        header = method.split("\n", 1)[0]
        if not re.search(r"\((Tier1|FullOpts|Tier1-OSR)\)", header):
            continue
        lines = method.split("\n")
        labels = {m.group(1): i for i, l in enumerate(lines) if (m := LABEL.match(l))}
        edges = [(labels[m.group(2)], j) for j, l in enumerate(lines) if (m := JUMP.match(l)) and labels.get(m.group(2), j) < j]
        inner = [e for e in edges if not any(o != e and e[0] <= o[0] and o[1] <= e[1] for o in edges)]
        seen = set()
        for a, b in inner:
            body = [c for l in lines[a + 1:b + 1] if not LABEL.match(l) and (c := clean(l))]
            if not any(register in c for c in body):
                continue
            body = [re.sub(r"G_M\d+_IG\d+", "LOOP", "call LOOP" if c.startswith("call") else c) for c in body]
            calls = sum(1 for l in lines[a:b + 1] if re.match(r"\s+call\s", l))
            shape = "\n".join(re.sub(r"\b(r\d+[dwb]?|[re]?[a-ds][xilp]|[xyz]mm\d+|k\d)\b", "R", c) for c in body)
            if shape in seen:
                continue
            seen.add(shape)
            # a masked word's loop shifts the word's bits to the vector's rows: then tests them as a vector (kortest) or,
            # since the skip moved ahead of the lane mask, shifts them back up and tests them as an integer
            shifts = any(c.startswith(("shrx", "shr ")) for c in body)
            masked = shifts and any(c.startswith(("kortest", "shlx", "shl ")) for c in body)
            yield job(header), "masked" if masked else "whole", body, calls


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--reg", default="zmm")
    parser.add_argument("--cpus", default="icelake-server,znver4")
    parser.add_argument("--keep")
    parser.add_argument("--all", action="store_true")
    a = parser.parse_args()
    cpus = a.cpus.split(",")
    folder = a.keep or tempfile.mkdtemp(prefix="loopbound-")
    os.makedirs(folder, exist_ok=True)

    def row(name, kind, count, path, calls=0):
        cells = []
        for cpu in cpus:
            b = bound(path, cpu)
            cells.append("- | -" if b is None else f"{b[0]:.1f} \\| {b[1]:.1f} / {b[2]:.1f} / {b[3]} | {b[5]}")
        print(f"| {name} | {kind} | {count}{f' ({calls} calls)' if calls else ''} | " + " | ".join(cells) + " |")

    print("| Job | Loop | Instr. | " + " | ".join(f"{c} | " for c in cpus) + "|")
    for given in a.inputs:
        if given.endswith(".s"):
            row(os.path.basename(given), "file", sum(1 for l in open(given) if l.strip() and not l.startswith(".") and not l.strip().endswith(":")), given)
            continue
        shown = set()
        for n, (name, kind, body, calls) in enumerate(loops(given, a.reg)):
            if not a.all and (calls or (name, kind) in shown):
                continue
            shown.add((name, kind))
            path = os.path.join(folder, f"{name}_{n}.s")
            open(path, "w").write(".intel_syntax noprefix\nLOOP:\n" + "\n".join(body) + "\n")
            row(name, kind, len(body), path, calls)
    print(f"llvm-mca inputs: {folder}", file=sys.stderr)


if __name__ == "__main__":
    main()
