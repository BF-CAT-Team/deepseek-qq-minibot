"""
本地假模型服务：让整条链路（组装 → 调用 → 气泡 → 轮换入库）在没有真实 API Key 的情况下跑通。

用法：
    python tools/fake_llm.py 9999

然后在配置页里填：
    Base URL = http://127.0.0.1:9999
    API Key  = 随便填

它会在收到"总结任务"时返回一条合法的记忆行，让轮换入库也能被验证。
每收到一条请求会在当前目录追加一行 fake_llm_requests.log，便于排查。
"""

import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CHAT_REPLY = "六点半吧，老地方？"
SUMMARY_REPLY = "NEW_EVENT|群友01|昨天下午|本地测试群|聚餐,火锅|约在周五晚上七点吃火锅"


def read_body(handler):
    """同时支持 Content-Length 与 chunked（.NET 的 JsonContent 默认会走 chunked）。"""
    if "chunked" in (handler.headers.get("Transfer-Encoding") or "").lower():
        data = b""
        while True:
            line = handler.rfile.readline().strip()
            if not line:
                continue
            size = int(line, 16)
            if size == 0:
                handler.rfile.readline()
                break
            data += handler.rfile.read(size)
            handler.rfile.read(2)
        return data

    length = int(handler.headers.get("Content-Length") or 0)
    return handler.rfile.read(length) if length else b""


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def do_POST(self):
        raw = read_body(self)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except Exception:
            payload = {}

        messages = payload.get("messages") or []
        last = messages[-1].get("content", "") if messages else ""
        is_summary = "【任务】" in last

        try:
            with open("fake_llm_requests.log", "a", encoding="utf-8") as fh:
                fh.write(
                    f"[{'SUMMARY' if is_summary else 'CHAT'}] n={len(messages)} "
                    f"tokens~{sum(len(m.get('content','')) for m in messages)} "
                    f"last={last[:60]!r}\n"
                )
        except Exception:
            pass

        content = SUMMARY_REPLY if is_summary else CHAT_REPLY
        body = {
            "id": "fake-1",
            "object": "chat.completion",
            "created": int(time.time()),
            "model": payload.get("model", "fake"),
            "choices": [
                {
                    "index": 0,
                    "message": {"role": "assistant", "content": content},
                    "finish_reason": "stop",
                }
            ],
            "usage": {
                "prompt_tokens": 1500,
                "completion_tokens": 40,
                "total_tokens": 1540,
                "prompt_cache_hit_tokens": 1300,
                "prompt_cache_miss_tokens": 200,
            },
        }

        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        time.sleep(0.2)  # 模拟一点网络延迟
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 9999
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"fake llm listening on http://127.0.0.1:{port}")
    server.serve_forever()
