import re
import json
import sys

if len(sys.argv) < 2:
    print("Usage: python debug_parse2.py <logfile>")
    sys.exit(1)

with open(sys.argv[1], "r", encoding="utf-8") as f:
    lines = f.readlines()

print(f"Total lines: {len(lines)}")
for i, line in enumerate(lines[:30]):
    match = re.search(r"\{.*\}", line)
    if not match:
        continue
    try:
        data = json.loads(match.group())
    except Exception as e:
        print(f"Line {i}: JSON parse error: {e}")
        continue

    keys = list(data.keys())
    print(f"Line {i}: top keys={keys}")
    ctx = data.get("context", {})
    if isinstance(ctx, dict):
        an = ctx.get("accountName", "")
        if an:
            print(f"  -> context.accountName={an}")
    break
