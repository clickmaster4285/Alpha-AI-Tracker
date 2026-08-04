#!/usr/bin/env python3
"""
Alpha AI Tracker — Native Messaging Host
Reads messages from the browser extension via stdin (Native Messaging protocol),
forwards them to the tracker client's Unix domain socket.

Native Messaging protocol:
- Each message is prefixed by a 32-bit native-endian integer (message length)
- The message body is UTF-8 JSON
- Responses are written back via stdout using the same format
"""

import sys
import json
import struct
import socket
import os
import traceback

# Socket path — matches the tracker client's socket path
SOCKET_PATH = os.path.expanduser("~/.local/share/alpha-ai-tracker/native-messaging.sock")
SOCKET_TIMEOUT = 3.0  # seconds


def read_message():
    """Read one message from stdin in Native Messaging format."""
    raw_length = sys.stdin.buffer.read(4)
    if not raw_length or len(raw_length) < 4:
        return None
    message_length = struct.unpack("=I", raw_length)[0]
    if message_length > 1024 * 1024:  # 1MB max
        return None
    message = sys.stdin.buffer.read(message_length).decode("utf-8")
    return json.loads(message)


def write_message(message):
    """Write one message to stdout in Native Messaging format."""
    encoded = json.dumps(message).encode("utf-8")
    sys.stdout.buffer.write(struct.pack("=I", len(encoded)))
    sys.stdout.buffer.write(encoded)
    sys.stdout.buffer.flush()


def forward_to_tracker(payload):
    """Forward a message to the tracker client via Unix socket.
    Creates a fresh connection for each message (NativeMessageService closes
    the accepted socket after handling one message).

    Uses a context manager to guarantee sock.close() is called even on
    exceptions (e.g. socket.timeout). Without this, the open connection
    leaks on the NativeMessageService side and stalls its single-threaded
    accept loop, eventually filling the backlog and causing ECONNREFUSED.
    """
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
            sock.settimeout(SOCKET_TIMEOUT)
            sock.connect(SOCKET_PATH)
            sock.sendall(json.dumps(payload).encode("utf-8"))
            # Read response
            response = sock.recv(4096)
            if response:
                return json.loads(response.decode("utf-8"))
            return {"status": "ok"}
    except (FileNotFoundError, ConnectionRefusedError):
        # Tracker client not running — silently drop
        return {"status": "error", "detail": "tracker not running"}
    except socket.timeout:
        return {"status": "error", "detail": "timeout"}
    except (BrokenPipeError, ConnectionResetError):
        # Tracker closed connection — can happen if it shut down
        return {"status": "error", "detail": "connection reset"}
    except Exception:
        return {"status": "error", "detail": "forward failed"}


def main():
    """Main loop: read messages from browser, forward to tracker."""
    # Ensure socket directory exists
    os.makedirs(os.path.dirname(SOCKET_PATH), exist_ok=True)

    while True:
        try:
            msg = read_message()
            if msg is None:
                break

            # Ping messages — forward to tracker AND acknowledge
            if msg.get("action") == "ping":
                forward_to_tracker(msg)  # fire-and-forget so tracker records heartbeat
                write_message({"status": "ok", "detail": "pong"})
                continue

            # Forward to tracker client
            response = forward_to_tracker(msg)
            write_message(response)

        except EOFError:
            break
        except Exception as e:
            # Log error but don't crash — browser extension might reconnect
            try:
                write_message({"status": "error", "detail": str(e)})
            except Exception:
                pass


if __name__ == "__main__":
    main()
