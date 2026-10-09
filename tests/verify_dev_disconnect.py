"""Live Debug regression: abandoned read requests must not break UIUpdate."""
import json
import socket
import struct
import time
import urllib.error
import urllib.request
from pathlib import Path

base = "http://127.0.0.1:8644"
log = Path.home() / "AppData/LocalLow/Colossal Order/Cities Skylines II/Player.log"
offset = log.stat().st_size
for _ in range(40):
    with socket.create_connection(("127.0.0.1", 8644), timeout=5) as client:
        client.sendall(b"GET /state HTTP/1.1\r\nHost: localhost\r\n\r\n")
        client.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("HH", 1, 0))
time.sleep(3)
with urllib.request.urlopen(base + "/state", timeout=20) as response:
    state = json.load(response)
    assert response.status == 200 and len(state["types"]) == 10
try:
    urllib.request.urlopen(base + "/unavailable", timeout=20)
    raise AssertionError("Expected 404")
except urllib.error.HTTPError as error:
    assert error.code == 404 and json.load(error)["error"] == "Unavailable"
with log.open("rb") as stream:
    stream.seek(offset)
    fresh = stream.read().decode("utf8", errors="replace")
assert "Cannot be changed after headers are sent" not in fresh
assert "UIUpdate->DevServerSystem" not in fresh
print("PASS: 40 abandoned requests, subsequent state 200, JSON 404, no DevServer update error")
