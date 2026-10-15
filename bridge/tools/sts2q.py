#!/usr/bin/env python3
"""Send one command to a running Sts2Bridge through its file queue (no network, no MCP needed).

Useful from a sandbox that can only reach the game's data folder (e.g. a cloud agent with that folder linked).

  python3 sts2q.py [--data DIR] [--client 1001] [--timeout 30] <command> ['{"json":"args"}']

  python3 sts2q.py state
  python3 sts2q.py --client 1002 buttons '{"match":"proceed"}'
  python3 sts2q.py console '{"command":"room shop"}'
  python3 sts2q.py screenshot '{"maxWidth":1280}'      # writes screenshot-<client>.png and prints its path

The data folder is the game's user data folder (%APPDATA%/SlayTheSpire2 on Windows). Default: $STS2_DATA_DIR,
else the first of ~/AppData/Roaming/SlayTheSpire2, ~/mnt/SlayTheSpire2 that exists.
"""
import argparse
import base64
import json
import os
import sys
import time
import uuid


def default_data_dir():
    if os.environ.get("STS2_DATA_DIR"):
        return os.environ["STS2_DATA_DIR"]
    home = os.path.expanduser("~")
    for candidate in (os.path.join(home, "AppData", "Roaming", "SlayTheSpire2"), os.path.join(home, "mnt", "SlayTheSpire2")):
        if os.path.isdir(candidate):
            return candidate
    sys.exit("can't find the game's data folder; pass --data or set STS2_DATA_DIR")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", default=None)
    ap.add_argument("--client", default="1001", help="client id of the game copy (default 1001, the host)")
    ap.add_argument("--timeout", type=float, default=30)
    ap.add_argument("cmd")
    ap.add_argument("args", nargs="?", default="{}")
    a = ap.parse_args()

    data = a.data or default_data_dir()
    queue = os.path.join(data, "bridge", "queue", a.client)
    inbox, outbox = os.path.join(queue, "in"), os.path.join(queue, "out")
    if not os.path.isdir(inbox):
        sys.exit(f"no bridge queue at {queue}: is a game copy with the bridge running for client {a.client}?")

    name = f"{int(time.time() * 1000)}-{uuid.uuid4().hex[:8]}"
    request = {"id": 1, "cmd": a.cmd, "args": json.loads(a.args)}
    tmp = os.path.join(inbox, name + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(request, f)
    os.replace(tmp, os.path.join(inbox, name + ".json"))

    reply_path = os.path.join(outbox, name + ".json")
    deadline = time.time() + a.timeout
    while not os.path.exists(reply_path):
        if time.time() > deadline:
            sys.exit(f"no reply within {a.timeout:.0f}s (is the game paused, loading, or closed?)")
        time.sleep(0.1)
    time.sleep(0.05)
    with open(reply_path, encoding="utf-8") as f:
        reply = json.load(f)
    try:
        os.remove(reply_path)
    except OSError:
        pass  # the bridge removes old replies itself

    if not reply.get("ok"):
        sys.exit(f"error: {reply.get('error')}")
    result = reply.get("result")
    if a.cmd == "screenshot" and isinstance(result, dict) and "data" in result:
        ext = "jpg" if "jpeg" in result.get("mime", "") else "png"
        out = os.path.abspath(f"screenshot-{a.client}.{ext}")
        with open(out, "wb") as f:
            f.write(base64.b64decode(result["data"]))
        print(json.dumps({"file": out, "width": result.get("width"), "height": result.get("height")}))
    else:
        print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
