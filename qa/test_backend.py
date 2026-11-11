"""End-to-end tests against a local simulated OpenAI-compatible provider. No real keys."""
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from xml.sax.saxutils import escape

PROJECT = Path(__file__).resolve().parents[1]
RESULTS = PROJECT / "qa" / "ai-backend"
RESULTS.mkdir(parents=True, exist_ok=True)
DATA = RESULTS / ("session-" + str(time.time_ns()))
DATA.mkdir()
TOKEN = "test-local-token-" + "a" * 48
SECRET = "test-dpapi-not-a-real-key-917263"
requests = []
results = []
mode = "sse"
search_mode = "rss"
search_requests = []
planner_mode = "json"
planner_query = "边缘行者2"


class Provider(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def handle(self):
        try:
            super().handle()
        except (ConnectionResetError, ConnectionAbortedError):
            pass  # Expected when cancellation/desktop shutdown closes its connection pool.

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query).get("q", [""])[0]
        search_requests.append({"query": query, "authorization": self.headers.get("Authorization")})
        current = search_mode
        payload = ('<?xml version="1.0" encoding="utf-8"?><rss version="2.0"><channel>'
                   '<item><title>联网测试文档</title><link>https://example.com/search-test</link>'
                   '<description>线上公告代号：银月42。' + escape(query) + '</description></item>'
                   '<item><title>不安全链接</title><link>javascript:alert(1)</link><description>无</description></item>'
                   '<item><title>重复结果</title><link>https://example.com/search-test</link><description>重复</description></item>'
                   '</channel></rss>').encode('utf-8')
        if current == "unrelated":
            payload = b"<rss><channel><item><title>Unrelated encyclopedia</title><link>https://example.com/other</link><description>Nothing relevant.</description></item></channel></rss>"
        elif current == "empty":
            payload = b'<rss><channel/></rss>'
        elif current == "broken":
            payload = b'<rss><not-closed>'
        elif current == "dtd":
            payload = b'<!DOCTYPE rss [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><rss><channel><item><title>&x;</title></item></channel></rss>'
        elif current == "large":
            payload = b'x' * (600 * 1024)
        self.send_response(503 if current == "error" else 200)
        self.send_header('Content-Type', 'application/rss+xml; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        try:
            if current == "slow":
                time.sleep(2)
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    def do_POST(self):
        global mode
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        requests.append({"path": self.path, "body": body, "authorization": self.headers.get("Authorization")})
        if body["messages"][0]["content"].startswith("你是联网搜索的检索词规划器"):
            content = json.dumps({"query": planner_query}, ensure_ascii=False) if planner_mode == "json" else "invalid planner reply"
            payload = json.dumps({"choices": [{"message": {"content": content}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return
        local_mode = "slow" if body["messages"][-1]["content"] == "思考动作取消验证" else mode
        if body["messages"][-1]["content"] == "思考动作错误验证":
            local_mode = "http_error"
        if local_mode == "http_error":
            payload = json.dumps({"error": {"message": SECRET}}).encode()
            self.send_response(401)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return
        if local_mode == "json":
            payload = json.dumps({"choices": [{"message": {"content": "非流式兼容回复"}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Connection", "close")
        self.end_headers()
        try:
            parts = ["你好，", "热牛奶在星光咖啡店供应，", "营业到晚上九点。[K1]"]
            if local_mode == "slow":
                parts = ["缓慢回复"] * 200
            for part in parts:
                chunk = {"choices": [{"delta": {"content": part}, "finish_reason": None}]}
                self.wfile.write(("data: " + json.dumps(chunk, ensure_ascii=False) + "\n\n").encode())
                self.wfile.flush()
                time.sleep(0.08 if local_mode != "slow" else 0.15)
            if local_mode != "truncated":
                self.wfile.write(b'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n')
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        self.close_connection = True


server = ThreadingHTTPServer(("127.0.0.1", 0), Provider)
threading.Thread(target=server.serve_forever, daemon=True).start()
provider_url = f"http://127.0.0.1:{server.server_port}/v1"
env = os.environ.copy()
env["HANEKAWA_LOCAL_TOKEN"] = TOKEN
env["HANEKAWA_SEARCH_ENDPOINT"] = f"http://127.0.0.1:{server.server_port}/search"
env["HANEKAWA_WEATHER_DISABLED"] = "1"
env.pop("HANEKAWA_AI_API_KEY", None)
ready = DATA / "ready.json"
backend_dll = PROJECT / "backend/bin/Debug/net9.0/HanekawaPet.Backend.dll"
log = open(DATA / "process.log", "w", encoding="utf-8")
process = subprocess.Popen(["dotnet", str(backend_dll), "--port", "0", "--data-dir", str(DATA), "--ready-file", str(ready)], env=env, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)


def check(name, fn):
    try:
        fn()
        results.append({"name": name, "passed": True})
        print("PASS", name, flush=True)
    except Exception as exc:
        results.append({"name": name, "passed": False, "error": repr(exc)})
        print("FAIL", name, repr(exc), flush=True)


def expect(condition, message="assertion failed"):
    if not condition:
        raise AssertionError(message)


def api(method, path, body=None, token=TOKEN, status=200):
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body, ensure_ascii=False).encode(), method=method, headers={"Content-Type": "application/json", "X-Pet-Token": token})
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read().decode("utf-8")
        expect(response.status == status, f"{method} {path}: HTTP {response.status}, expected {status}: {raw}")
        return json.loads(raw) if raw else None


def chat(cid, message):
    request = urllib.request.Request(base + "/api/chat/stream", data=json.dumps({"conversationId": cid, "message": message}).encode(), headers={"Content-Type": "application/json", "X-Pet-Token": TOKEN})
    with urllib.request.urlopen(request, timeout=30) as response:
        return [json.loads(line[6:]) for line in response.read().decode().splitlines() if line.startswith("data: ")]


try:
    for _ in range(200):
        if ready.exists():
            break
        if process.poll() is not None:
            raise RuntimeError("Backend exited: " + (DATA / "process.log").read_text())
        time.sleep(0.05)
    base = json.loads(ready.read_text())["url"]
    config = {"baseUrl": provider_url, "model": "mock-chat", "apiKey": SECRET, "timeoutSeconds": 10}
    check("loopback binding and health", lambda: (expect(base.startswith("http://127.0.0.1:")), expect(urllib.request.urlopen(base + "/health").status == 200)))
    def unauthorized():
        try:
            urllib.request.urlopen(base + "/api/config")
            raise AssertionError("Unauthenticated access allowed")
        except urllib.error.HTTPError as error:
            expect(error.code == 401)
    check("unauthenticated configuration denied", unauthorized)
    check("DPAPI encrypted credential and no readback", lambda: (
        api("PUT", "/api/config", config),
        expect(SECRET not in json.dumps(api("GET", "/api/config"))),
        expect(SECRET not in (DATA / "ai-config.json").read_text()),
        expect(api("GET", "/api/config")["hasApiKey"])))
    def preserve_key():
        saved = (DATA / "ai-config.json").read_text()
        api("PUT", "/api/config", {**config, "apiKey": ""})
        expect((DATA / "ai-config.json").read_text() == saved)
        api("POST", "/api/config/test", {})
        expect(requests[-1]["authorization"] == "Bearer " + SECRET)
        expect(requests[-1]["path"] == "/v1/chat/completions")
        expect("temperature" not in requests[-1]["body"] and "max_tokens" not in requests[-1]["body"])
    check("key preservation, auth header, URL and optional params", preserve_key)
    check("invalid configuration rejected", lambda: api("PUT", "/api/config", {**config, "baseUrl": "https://provider.invalid/v1?key=oops"}, status=400))
    api("DELETE", "/api/knowledge?id=" + urllib.parse.quote("persona/角色设定.md"))
    def import_files():
        for name, kind, content in [
            ("猫咪设定.md", "persona", "名字：月喵。性格温柔，最喜欢热牛奶。说话带一点俏皮。"),
            ("背景.json", "persona", json.dumps({"rules": ["不要编造事实"], "speakingStyle": "简短中文"}, ensure_ascii=False)),
            ("咖啡资料.json", "documents", json.dumps({"documents": [{"title": "星光咖啡店", "content": "星光咖啡店供应热牛奶，营业时间下午两点到晚上九点。"}, {"title": "无关资料", "content": "量子实验室的设备每年校准。"}]}, ensure_ascii=False)),
            ("常识.jsonl", "documents", '{"question":"猫咪喜欢什么？","answer":"猫咪喜欢热牛奶。"}\n{"title":"约定","content":"周五整理桌面。"}'),
            ("长资料.md", "documents", "远方的故事。" * 180 + "\n星光咖啡店后门周末关闭。"),
        ]:
            api("POST", "/api/knowledge/import", {"fileName": name, "kind": kind, "content": content})
        expect(len(api("GET", "/api/knowledge")["files"]) == 5)
    check("Markdown, JSON and JSONL persona and document ingestion", import_files)
    def invalid_files():
        api("POST", "/api/knowledge/import", {"fileName": "常识.jsonl", "kind": "documents", "content": '{"ok":1}\ninvalid'}, status=400)
        expect("热牛奶" in (DATA / "knowledge/documents/常识.jsonl").read_text(encoding="utf-8"))
        for name in ["../escape.md", "a.txt", "..\\escape.json"]:
            api("POST", "/api/knowledge/import", {"fileName": name, "kind": "documents", "content": "x"}, status=400)
        api("DELETE", "/api/knowledge?id=" + urllib.parse.quote("persona/../../ai-config.json"), status=400)
    check("malformed JSONL rollback and path traversal rejection", invalid_files)
    cid = api("POST", "/api/conversations", {})["id"]
    def prompt_and_stream():
        events = chat(cid, "星光咖啡店什么时候营业，有什么饮料？")
        expect(events[-1]["type"] == "done")
        sources = events[0]["sources"]
        expect(any(s["fileName"] == "咖啡资料.json" for s in sources), "Chinese retrieval missed coffee facts")
        expect(not any(s["fileName"] in ("猫咪设定.md", "背景.json") for s in sources), "persona incorrectly retrieved as document")
        body = requests[-1]["body"]
        messages = body["messages"]
        expect(messages[0]["role"] == "system" and "月喵" in messages[0]["content"] and "不要编造事实" in messages[0]["content"])
        expect(messages[-1]["content"] == "星光咖啡店什么时候营业，有什么饮料？")
        expect(any("仅供事实参考" in m["content"] for m in messages if m["role"] == "user"))
        expect(all(set(m) == {"role", "content"} for m in messages), "Internal citation metadata sent as unsupported API message fields")
        expect(len([e for e in events if e["type"] == "delta"]) == 3)
        expect(len(api("GET", "/api/conversations/" + cid)["messages"]) == 2)
    check("Chinese retrieval, prompt priority, SSE and atomic history", prompt_and_stream)
    def multi_turn():
        chat(cid, "那你喜欢什么？")
        messages = requests[-1]["body"]["messages"]
        expect(any(m["role"] == "assistant" and "晚上九点" in m["content"] for m in messages))
        expect(len(api("GET", "/api/conversations/" + cid)["messages"]) == 4)
    check("multi-turn history included", multi_turn)
    def reload():
        path = DATA / "knowledge/persona/猫咪设定.md"
        path.write_text("名字：银月。用中文回答。", encoding="utf-8")
        chat(cid, "你好")
        expect("银月" in requests[-1]["body"]["messages"][0]["content"])
        bad = DATA / "knowledge/persona/损坏.json"
        bad.write_text("{", encoding="utf-8")
        expect(any(f["error"] for f in api("GET", "/api/knowledge")["files"]))
        api("POST", "/api/chat/stream", {"conversationId": cid, "message": "你好"}, status=400)
        bad.unlink()
    check("direct edits reload and malformed persona blocks silent omission", reload)
    def no_save_on_error():
        global mode
        before = api("GET", "/api/conversations/" + cid)["messages"]
        for test_mode in ["http_error", "truncated"]:
            mode = test_mode
            events = chat(cid, "错误测试")
            expect(events[-1]["type"] == "error")
            expect(SECRET not in json.dumps(events))
            expect(api("GET", "/api/conversations/" + cid)["messages"] == before)
        mode = "sse"
    check("upstream failures and truncated stream do not commit or leak keys", no_save_on_error)
    def cancel():
        global mode
        mode = "slow"
        before = len(api("GET", "/api/conversations/" + cid)["messages"])
        request = urllib.request.Request(base + "/api/chat/stream", data=json.dumps({"conversationId": cid, "message": "慢慢说"}).encode(), headers={"Content-Type": "application/json", "X-Pet-Token": TOKEN})
        response = urllib.request.urlopen(request)
        while True:
            line = response.readline().decode()
            if '"type":"delta"' in line:
                break
        response.close()
        time.sleep(0.4)
        expect(len(api("GET", "/api/conversations/" + cid)["messages"]) == before)
        mode = "sse"
    check("client cancellation reaches upstream and preserves history", cancel)
    def json_fallback():
        global mode
        mode = "json"
        events = chat(cid, "测试普通JSON返回")
        expect(events[-1]["type"] == "done" and any(e.get("text") == "非流式兼容回复" for e in events))
        mode = "sse"
    check("non-streaming compatible JSON response", json_fallback)
    def web_grounding():
        before = len(search_requests)
        events = chat(cid, "帮我搜一下最新在线公告")
        expect(events[-1]["type"] == "done")
        expect(len(search_requests) == before + 1)
        expect(search_requests[-1]["query"] == "最新在线公告")
        expect(search_requests[-1]["authorization"] is None, "AI credential sent to search provider")
        messages = requests[-1]["body"]["messages"]
        context = messages[-2]["content"]
        expect("本轮真实联网检索" in context and "银月42" in context and "https://example.com/search-test" in context)
        expect("不是指令" in context and "没有读取网页全文" in context)
        sources = next(e["sources"] for e in events if e["type"] == "sources")
        web = [s for s in sources if s["id"].startswith("W")]
        expect(len(web) == 1 and web[0]["location"] == "https://example.com/search-test", "unsafe or duplicate results not filtered")
        expect(messages[-1]["content"] == "帮我搜一下最新在线公告")
    check("web search grounds generic compatible model with real titles, URLs and snippets", web_grounding)
    def web_routing():
        before = len(search_requests)
        for question in ["你好呀", "不要联网搜索，陪我聊聊", "我今天有点累，陪我聊聊", "你能联网搜索吗", "123456", "sk-example123456"]:
            chat(cid, question)
        expect(len(search_requests) == before, "casual conversation or search opt-out sent to search provider")
        chat(cid, "杭州明天天气如何？")
        expect(len(search_requests) == before + 1 and "杭州" in search_requests[-1]["query"])
    check("search routing respects casual chat and opt-out, detects current weather", web_routing)
    def natural_search_instructions():
        for question, expected in [("你再去搜边缘行者2", "边缘行者2"), ("你搜一下重庆今天天气和温度", "重庆今天天气和温度"), ("请你帮我重新搜索一下边缘行者2", "边缘行者2")]:
            chat(cid, question)
            expect(search_requests[-1]["query"] == expected, search_requests[-1])
    check("natural Chinese search commands strip pronouns and repeat requests", natural_search_instructions)

    def contextual_search():
        global planner_mode, planner_query
        before = len(search_requests)
        chat(cid, "边缘行者2")
        expect(len(search_requests) == before + 1 and search_requests[-1]["query"] == "边缘行者2")
        for question, variant in [("你说话之前联网搜索一下", "边缘行者 第二季"), ("为什么你搜得到天气搜不到这个", "边缘行者 2 续作 动画"), ("就是有你再搜，把你搜到的告诉我", "赛博朋克 边缘行者 第二季"), ("再查呢", "边缘行者第二季")]:
            planner_query = variant
            events = chat(cid, question)
            expect(search_requests[-1]["query"] == "边缘行者2", search_requests[-1])
            expect(any(s["id"].startswith("W") for e in events if e["type"] == "sources" for s in e["sources"]))
            expect(any(e.get("query") == "边缘行者2" for e in events if e["type"] == "status"))
        try:
            planner_mode = "invalid"
            chat(cid, "你说话之前联网搜索一下")
            expect(search_requests[-1]["query"] == "边缘行者2", "planner fallback lost the prior topic")
        finally:
            planner_mode = "json"
    check("bare topic and repeated search follow-ups retain conversational subject", contextual_search)
    def web_failures():
        global search_mode
        try:
            for current in ["empty", "error", "broken", "dtd", "large", "unrelated"]:
                search_mode = current
                events = chat(cid, "搜一下联网失败测试")
                expect(events[-1]["type"] == "done", current)
                reply = "".join(e.get("text", "") for e in events if e["type"] == "delta")
                expect("暂时无法核实" in reply and "不代表它不存在" in reply, current)
                expect(not any(s["id"].startswith("W") for e in events if e["type"] == "sources" for s in e["sources"]), current)
        finally:
            search_mode = "rss"
    check("empty, unavailable, malformed, DTD and oversized search results fail honestly", web_failures)
    def web_cancellation():
        global search_mode
        search_mode = "slow"
        before = len(api("GET", "/api/conversations/" + cid)["messages"])
        request = urllib.request.Request(base + "/api/chat/stream", data=json.dumps({"conversationId": cid, "message": "帮我搜一下慢速搜索测试"}).encode(), headers={"Content-Type": "application/json", "X-Pet-Token": TOKEN})
        try:
            with urllib.request.urlopen(request) as response:
                while True:
                    line = response.readline().decode()
                    if '"type":"status"' in line:
                        break
            time.sleep(0.3)
            expect(len(api("GET", "/api/conversations/" + cid)["messages"]) == before)
        finally:
            search_mode = "rss"
    check("cancel during web search saves no incomplete conversation", web_cancellation)
    def ui_smoke():
        if os.environ.get("HANEKAWA_SKIP_UI_TEST") == "1":
            return
        ui_data = DATA / "desktop"
        backend = ui_data / "backend"
        (backend / "knowledge/persona").mkdir(parents=True)
        (backend / "knowledge/documents").mkdir()
        (backend / ".knowledge-initialized").write_text("1")
        (backend / "ai-config.json").write_text(json.dumps({"baseUrl": provider_url, "model": "mock-chat", "timeoutSeconds": 10}))
        (backend / "knowledge/persona/角色.md").write_text("名字：月喵。温柔。", encoding="utf-8")
        (backend / "knowledge/documents/资料.md").write_text("星光咖啡店提供热牛奶，营业至晚上九点。", encoding="utf-8")
        exe = Path(os.environ.get("HANEKAWA_TEST_EXE", str(PROJECT / "bin/Debug/net9.0-windows/HanekawaPet.exe")))
        output = DATA / "ui"
        desktop = subprocess.Popen([str(exe), "--data-dir", str(ui_data), "--chat-smoke-test", str(output)], creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            desktop.wait(timeout=45)
            expect(desktop.returncode == 0, f"Desktop exit {desktop.returncode}")
            report = json.loads((output / "results.json").read_text(encoding="utf-8-sig"))
            expect(all(r["passed"] for r in report), str(report))
            child_url = (output / "backend-address.txt").read_text().strip()
            try:
                urllib.request.urlopen(child_url + "health", timeout=2)
            except urllib.error.URLError:
                pass
            else:
                raise AssertionError("Desktop-owned backend remained running after desktop exit")
        finally:
            if desktop.poll() is None:
                desktop.kill()
    check("desktop real-window chat, tabs, knowledge and service lifecycle", ui_smoke)
    def clear_key():
        api("PUT", "/api/config", {**config, "apiKey": "", "clearApiKey": True})
        expect(not api("GET", "/api/config")["hasApiKey"])
    check("explicit credential removal", clear_key)
finally:
    process.terminate()
    process.wait(timeout=10)
    log.close()
    server.shutdown()
    (RESULTS / "results.json").write_text(json.dumps({"dataDirectory": str(DATA), "results": results}, ensure_ascii=False, indent=2), encoding="utf-8")

print(f"{sum(r['passed'] for r in results)}/{len(results)} passed", flush=True)
raise SystemExit(0 if results and all(r["passed"] for r in results) else 1)
