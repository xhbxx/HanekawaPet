"""Verify failed/irrelevant primary RSS cannot suppress a working backup. No real keys."""
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "qa/search-fallback" / ("session-" + str(time.time_ns()))
OUT.mkdir(parents=True)
mode = "error"
seen = []

class Provider(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_GET(self):
        seen.append(dict(path=self.path, auth=self.headers.get("Authorization")))
        if self.path.startswith("/fallback"):
            body = '<rss><channel><item><title>边缘行者2 官方公告</title><link>https://www.bing.com/news/apiclick.aspx?url=https%3A%2F%2Fexample.com%2Fannouncement</link><description>续作已公布</description><pubDate>Thu, 22 Oct 2026 12:00:00 GMT</pubDate></item></channel></rss>'.encode()
        elif mode == "unrelated":
            body = b'<rss><channel><item><title>Unrelated</title><link>https://example.com/wrong</link><description>Other topic</description></item></channel></rss>'
        elif mode == "broken": body = b'<html>Access denied</html>'
        else: body = b'Unavailable'
        self.send_response(503 if mode == "error" and not self.path.startswith("/fallback") else 200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_POST(self):
        self.rfile.read(int(self.headers["Content-Length"]))
        body = json.dumps({"choices": [{"message": {"content": "备选入口有真实资料"}}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

server = ThreadingHTTPServer(("127.0.0.1", 0), Provider)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{server.server_port}"
(OUT / "ai-config.json").write_text(json.dumps(dict(baseUrl=url + "/v1", model="mock")))
env = os.environ.copy()
token = "fallback-token-" + "a" * 48
env.update(HANEKAWA_LOCAL_TOKEN=token, HANEKAWA_WEATHER_DISABLED="1", HANEKAWA_SEARCH_ENDPOINT=url + "/primary", HANEKAWA_SEARCH_FALLBACK_ENDPOINT=url + "/fallback")
ready = OUT / "ready.json"
process = subprocess.Popen(["dotnet", str(ROOT / "backend/bin/Debug/net9.0/HanekawaPet.Backend.dll"), "--port", "0", "--data-dir", str(OUT), "--ready-file", str(ready)], env=env, creationflags=subprocess.CREATE_NO_WINDOW)
results = []
try:
    for _ in range(200):
        if ready.exists(): break
        if process.poll() is not None: raise RuntimeError("Backend exited")
        time.sleep(.05)
    base = json.loads(ready.read_text())["url"]
    def api(path, value):
        return urllib.request.urlopen(urllib.request.Request(base + path, data=json.dumps(value, ensure_ascii=False).encode(), headers={"Content-Type": "application/json", "X-Pet-Token": token}), timeout=30)
    with api("/api/conversations", {}) as response: cid = json.load(response)["id"]
    for mode in ["error", "unrelated", "broken"]:
        before = len(seen)
        with api("/api/chat/stream", dict(conversationId=cid, message="边缘行者2")) as response:
            events = [json.loads(line[6:]) for line in response.read().decode().splitlines() if line.startswith("data: ")]
        sources = [s for e in events if e["type"] == "sources" for s in e["sources"] if s["id"].startswith("W")]
        trace = json.loads((OUT / "last-search.json").read_text())
        assert events[-1]["type"] == "done" and sources[0]["location"] == "https://example.com/announcement"
        assert len(seen) == before + 2 and all(s["auth"] is None for s in seen)
        assert len(trace["attempts"]) == 2 and trace["attempts"][-1]["outcome"] == "ok" and trace["results"] == 1
        results.append(dict(mode=mode, passed=True, attempts=trace["attempts"]))
        print("PASS", mode, flush=True)
finally:
    process.terminate(); process.wait(timeout=10); server.shutdown()
    (OUT / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print("Report:", OUT / "results.json")
