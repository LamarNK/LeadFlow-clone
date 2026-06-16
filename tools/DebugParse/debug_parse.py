import re
import json
import sys

if len(sys.argv) < 2:
    print("Usage: python debug_parse.py <logfile>")
    sys.exit(1)

with open(sys.argv[1], "r", encoding="utf-8") as f:
    lines = f.readlines()

print(f"Total lines: {len(lines)}")
count = 0
for i, line in enumerate(lines[:100]):
    match = re.search(r"\{.*\}", line)
    if not match:
        continue
    try:
        data = json.loads(match.group())
    except Exception as e:
        print(f"Line {i}: JSON parse error: {e}")
        print(f"  JSON excerpt: {match.group()[:200]}")
        continue
    account_name = data.get("accountName", "")
    if "Avito" in str(account_name):
        count += 1
        msg = data.get("message", "")
        print(f"Line {i}: account={account_name}, msg={msg[:100] if msg else ''}")

print(f"Total Avito matches in first 100 lines: {count}")
