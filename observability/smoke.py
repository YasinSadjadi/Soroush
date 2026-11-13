"""Readiness/data checks for the local Compose stack. Uses only Python's standard library."""
import argparse
import json
import time
import urllib.parse
import urllib.request


def get(url):
    with urllib.request.urlopen(url, timeout=3) as response:
        return response.read().decode()


def eventually(description, check, seconds=60):
    deadline = time.monotonic() + seconds
    last_error = "not ready"
    while time.monotonic() < deadline:
        try:
            if check():
                print(f"OK: {description}", flush=True)
                return
        except Exception as error:
            last_error = str(error)
        time.sleep(2)
    raise RuntimeError(f"{description} did not pass within {seconds}s: {last_error}")


def ready():
    for name, url in [
        ("Collector", "http://127.0.0.1:13133/"),
        ("Loki", "http://127.0.0.1:3100/ready"),
        ("Tempo", "http://127.0.0.1:3200/ready"),
        ("Prometheus", "http://127.0.0.1:9090/-/ready"),
        ("Grafana", "http://127.0.0.1:3000/api/health"),
    ]:
        eventually(f"{name} ready", lambda url=url: bool(get(url)))


def data():
    query = "sum(soroush_mediator_operation_duration_seconds_count)"
    eventually("Mediator metrics in Prometheus", lambda: bool(
        json.loads(get("http://127.0.0.1:9090/api/v1/query?" + urllib.parse.urlencode({"query": query})))["data"]["result"]))
    eventually("Mediator logs in Loki", lambda: bool(
        json.loads(get("http://127.0.0.1:3100/loki/api/v1/query_range?" +
                       urllib.parse.urlencode({"query": '{service_name="soroush-diagnostics-demo"}', "limit": 5})))["data"]["result"]))
    eventually("Mediator traces in Tempo", lambda: bool(
        json.loads(get("http://127.0.0.1:3200/api/search?" +
                       urllib.parse.urlencode({"q": '{resource.service.name="soroush-diagnostics-demo"}', "limit": 5}))).get("traces")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", action="store_true")
    arguments = parser.parse_args()
    data() if arguments.data else ready()
