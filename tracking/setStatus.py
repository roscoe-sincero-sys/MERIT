"""Set task statuses in tasks.json:  python tracking/setStatus.py T010=Done T011=Done "T020=In progress" ..."""
import json, sys
from datetime import date
from pathlib import Path
p = Path(__file__).with_name("tasks.json")
data = json.loads(p.read_text(encoding="utf-8"))
byid = {t["id"]: t for t in data["tasks"]}
today = date.today().isoformat()
for arg in sys.argv[1:]:
    tid, _, rest = arg.partition("=")
    status, _, note = rest.partition("|")
    t = byid[tid]
    t["status"] = status
    t.setdefault("started", today)
    if status == "Done":
        t["finished"] = today
        t.pop("pct", None)
    if note:
        t["notes"] = note
p.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
print(f"updated {len(sys.argv) - 1} task(s)")
