#!/usr/bin/python3
"""Fixture observer for the actual native process started by the repair controller."""

import argparse
import http.client
import json
import os
from urllib.parse import urlsplit
import uuid


MODEL = "published-smoke-model"
REASONING_MARKER = "fixture-hidden-marker"
WIRE_PROOF = "continuation-wire-proved"
SEED_PROMPT = "preserve-original-session"
SEED_ANSWER = "original-effect-recorded"
CONTRACT = "ollama-reasoning-continuation-v1"


def request(origin, key, route, payload=None, content_type="application/json"):
    target = urlsplit(origin)
    if target.scheme != "http" or target.hostname != "127.0.0.1" or target.port is None:
        raise ValueError("The fixture host must use an explicit IPv4 loopback port.")
    if target.netloc != f"127.0.0.1:{target.port}" or target.path or target.query or target.fragment:
        raise ValueError("The fixture host must be a bare origin.")

    connection = http.client.HTTPConnection("127.0.0.1", target.port, timeout=45)
    try:
        body = payload
        if payload is not None and not isinstance(payload, bytes):
            body = json.dumps(payload).encode()
        headers = {"X-Arcanum-Key": key, "Content-Type": content_type}
        connection.request("GET" if body is None else "POST", route, body, headers)
        response = connection.getresponse()
        data = response.read(4_194_305)
        if response.status not in (200, 201, 202) or len(data) > 4_194_304:
            raise ValueError(f"The fixture host refused an HTTP request ({response.status}).")
        return data
    finally:
        connection.close()


def envelope(payload):
    response = json.loads(payload)
    if response.get("isSuccess") is not True:
        raise ValueError("The fixture host reported an unsuccessful operation.")
    return response["data"]


def turn(session, prompt):
    return {
        "prompt": prompt, "sessionId": session, "model": MODEL,
        "disableMcpTools": True, "skipSpellRouting": True, "reasoning": {"output": "none"},
    }


def verify(origin, key, session, process_id, activation_id):
    metadata = envelope(request(origin, key, "/api/meta"))
    retained = envelope(request(origin, key, f"/api/sessions/{session}"))
    if (metadata["processId"] != process_id or metadata["nativeAot"] is not True
            or retained["id"] != session or retained["entryCount"] < 4):
        raise ValueError("The restarted apphost did not reopen the expected Session.")

    for suffix in ("buffered", "stream"):
        prompt = f"verify-installed-repair-{activation_id}-{suffix}"
        streamed = suffix == "stream"
        route = "/api/intelligence/ping-stream" if streamed else "/api/intelligence/ping"
        payload = request(origin, key, route, {**turn(session, prompt), "maxOutputTokens": 32})
        if streamed:
            records = [json.loads(line) for line in payload.splitlines() if line.strip()]
            if records[-1]["type"] != "result" or any(record["type"] == "error" for record in records):
                raise ValueError("The installed apphost did not complete the streamed tool continuation.")
        else:
            envelope(payload)
        if WIRE_PROOF.encode() not in payload or REASONING_MARKER.encode() in payload:
            raise ValueError("The installed apphost failed the reasoning-continuation wire check.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-url", required=True)
    parser.add_argument("--session", required=True, type=uuid.UUID)
    parser.add_argument("--process-id", required=True, type=int)
    parser.add_argument("--apphost-sha256", required=True)
    parser.add_argument("--activation-id", required=True, type=uuid.UUID)
    args = parser.parse_args()
    session = str(args.session)
    activation_id = str(args.activation_id)
    try:
        verify(args.api_url, os.environ["ARCANUM_API_KEY"], session, args.process_id, activation_id)
    except (OSError, ValueError, KeyError, IndexError, http.client.HTTPException):
        return 1
    print(json.dumps({
        "contract": CONTRACT, "activationId": activation_id, "sessionId": session,
        "processId": args.process_id, "apphostSha256": args.apphost_sha256, "disposition": "passed",
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
