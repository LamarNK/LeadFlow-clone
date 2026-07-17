#!/usr/bin/env python3
"""Export russiannames parquet → compact gzip TSV for Orbita.Contracts embedded resource.

Source: https://github.com/datacoon/russiannames (BSD-3-Clause)
"""
from __future__ import annotations

import gzip
from pathlib import Path

import pyarrow.parquet as pq

ROOT = Path(__file__).resolve().parent
OUT_DIR = ROOT.parents[1] / "Orbita.Contracts" / "Data"
SURNAME_TOP_N = 120_000


def norm(text: str) -> str:
    return (text or "").strip().casefold()


def collect(table, top_n: int | None = None):
    texts = table.column("text").to_pylist()
    genders = table.column("gender").to_pylist()
    counts = table.column("count").to_pylist()
    best: dict[str, tuple[int, str]] = {}
    for text, gender, count in zip(texts, genders, counts):
        if gender not in ("m", "f") or not text:
            continue
        t = norm(text)
        if not t or len(t) < 2:
            continue
        c = int(count or 0)
        prev = best.get(t)
        if prev is None or c > prev[0]:
            best[t] = (c, gender)
        elif c == prev[0] and gender != prev[1]:
            best[t] = (c, "u")  # conflict → drop later

    items = [(c, t, g) for t, (c, g) in best.items() if g in ("m", "f")]
    items.sort(key=lambda x: -x[0])
    if top_n is not None:
        items = items[:top_n]
    return items


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    names = collect(pq.read_table(ROOT / "names.parquet"))
    mids = collect(pq.read_table(ROOT / "midnames.parquet"))
    surnames = collect(pq.read_table(ROOT / "surnames.parquet"), top_n=SURNAME_TOP_N)

    # Promote f_form female variants missing as explicit rows.
    st = pq.read_table(ROOT / "surnames.parquet")
    f_forms = st.column("f_form").to_pylist()
    counts = st.column("count").to_pylist()
    existing = {t for _, t, _ in surnames}
    extra: list[tuple[int, str, str]] = []
    for ff, c in zip(f_forms, counts):
        if not ff:
            continue
        t = norm(ff)
        if len(t) < 2 or t in existing:
            continue
        extra.append((int(c or 0), t, "f"))
        existing.add(t)
    extra.sort(key=lambda x: -x[0])
    for c, t, g in extra[:20_000]:
        surnames.append((c, t, g))

    best: dict[str, tuple[int, str]] = {}
    for c, t, g in surnames:
        if t not in best or c > best[t][0]:
            best[t] = (c, g)
    surnames = sorted(
        [(c, t, g) for t, (c, g) in best.items() if g in ("m", "f")],
        key=lambda x: -x[0],
    )[:SURNAME_TOP_N]

    out_path = OUT_DIR / "russian-name-gender.tsv.gz"
    with gzip.open(out_path, "wt", encoding="utf-8", newline="\n") as f:
        f.write("# source: https://github.com/datacoon/russiannames (BSD-3-Clause)\n")
        f.write("# format: kind\\ttext\\tgender  kind=n|m|s  gender=m|f\n")
        for _, t, g in names:
            f.write(f"n\t{t}\t{g}\n")
        for _, t, g in mids:
            f.write(f"m\t{t}\t{g}\n")
        for _, t, g in surnames:
            f.write(f"s\t{t}\t{g}\n")

    print(f"names={len(names)} midnames={len(mids)} surnames={len(surnames)}")
    print(f"wrote {out_path} ({out_path.stat().st_size} bytes)")

    checks = {
        "n:ринат": None,
        "n:зинаида": None,
        "m:ахметович": None,
        "s:петрова": None,
        "s:нигматуллин": None,
        "n:фарход": None,
    }
    with gzip.open(out_path, "rt", encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            k, t, g = line.rstrip("\n").split("\t")
            key = f"{k}:{t}"
            if key in checks:
                checks[key] = g
    print("spot checks:", checks)


if __name__ == "__main__":
    main()
