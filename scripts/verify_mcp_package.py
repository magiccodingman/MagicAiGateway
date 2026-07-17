#!/usr/bin/env python3
"""Exercise a compiled MagicAiGateway MCP package exclusively through ABI v1."""

from __future__ import annotations

import argparse
import ctypes
import json
from pathlib import Path
from typing import Any

SUCCESS = 0
NO_MESSAGE = 1
INVALID_ARGUMENT = 2
INSTANCE_NOT_FOUND = 3
BUFFER_TOO_SMALL = 4
INSTANCE_STOPPED = 5


class NativePackage:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.library = ctypes.CDLL(str(path))
        size_pointer = ctypes.POINTER(ctypes.c_size_t)

        self.library.magic_mcp_get_abi_version.argtypes = []
        self.library.magic_mcp_get_abi_version.restype = ctypes.c_int
        self.library.magic_mcp_get_manifest.argtypes = [
            ctypes.c_void_p,
            ctypes.c_size_t,
            size_pointer,
        ]
        self.library.magic_mcp_get_manifest.restype = ctypes.c_int
        self.library.magic_mcp_start_instance.argtypes = [
            ctypes.c_void_p,
            ctypes.c_size_t,
            ctypes.c_void_p,
        ]
        self.library.magic_mcp_start_instance.restype = ctypes.c_int
        self.library.magic_mcp_send.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_size_t,
        ]
        self.library.magic_mcp_send.restype = ctypes.c_int
        self.library.magic_mcp_receive.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_size_t,
            size_pointer,
            ctypes.c_int,
        ]
        self.library.magic_mcp_receive.restype = ctypes.c_int
        self.library.magic_mcp_stop_instance.argtypes = [ctypes.c_void_p]
        self.library.magic_mcp_stop_instance.restype = ctypes.c_int
        self.library.magic_mcp_list_instances.argtypes = [
            ctypes.c_void_p,
            ctypes.c_size_t,
            size_pointer,
        ]
        self.library.magic_mcp_list_instances.restype = ctypes.c_int
        self.library.magic_mcp_shutdown.argtypes = []
        self.library.magic_mcp_shutdown.restype = ctypes.c_int
        self.library.magic_mcp_get_last_error.argtypes = [
            ctypes.c_void_p,
            ctypes.c_size_t,
            size_pointer,
        ]
        self.library.magic_mcp_get_last_error.restype = ctypes.c_int

    def last_error(self) -> str:
        required = ctypes.c_size_t()
        status = self.library.magic_mcp_get_last_error(
            None,
            0,
            ctypes.byref(required),
        )
        if status not in (SUCCESS, BUFFER_TOO_SMALL) or required.value == 0:
            return ""

        buffer = ctypes.create_string_buffer(required.value)
        status = self.library.magic_mcp_get_last_error(
            buffer,
            required.value,
            ctypes.byref(required),
        )
        if status != SUCCESS:
            return ""

        return bytes(buffer.raw[: required.value]).decode("utf-8")

    def check(self, status: int, operation: str, allowed: tuple[int, ...] = (SUCCESS,)) -> None:
        if status not in allowed:
            raise RuntimeError(
                f"{operation} returned status {status}: {self.last_error()}"
            )

    def manifest(self) -> dict[str, Any]:
        required = ctypes.c_size_t()
        status = self.library.magic_mcp_get_manifest(
            None,
            0,
            ctypes.byref(required),
        )
        self.check(status, "manifest size query", (BUFFER_TOO_SMALL,))

        buffer = ctypes.create_string_buffer(required.value)
        status = self.library.magic_mcp_get_manifest(
            buffer,
            required.value,
            ctypes.byref(required),
        )
        self.check(status, "manifest read")
        return json.loads(bytes(buffer.raw[: required.value]))

    def start(self, configuration: dict[str, Any]) -> "NativeInstance":
        payload = json.dumps(configuration, separators=(",", ":")).encode("utf-8")
        payload_buffer = ctypes.create_string_buffer(payload)
        instance_id = (ctypes.c_ubyte * 16)()

        status = self.library.magic_mcp_start_instance(
            payload_buffer,
            len(payload),
            instance_id,
        )
        self.check(status, "start instance")
        return NativeInstance(self, instance_id)

    def list_instances(self) -> set[bytes]:
        required = ctypes.c_size_t()
        status = self.library.magic_mcp_list_instances(
            None,
            0,
            ctypes.byref(required),
        )

        if required.value == 0:
            self.check(status, "list empty instances")
            return set()

        self.check(status, "list instance size query", (BUFFER_TOO_SMALL,))
        if required.value % 16 != 0:
            raise RuntimeError(
                f"Instance list returned invalid byte length {required.value}."
            )

        buffer = ctypes.create_string_buffer(required.value)
        status = self.library.magic_mcp_list_instances(
            buffer,
            required.value,
            ctypes.byref(required),
        )
        self.check(status, "list instances")

        raw = bytes(buffer.raw[: required.value])
        return {raw[index : index + 16] for index in range(0, len(raw), 16)}

    def shutdown(self) -> None:
        self.check(self.library.magic_mcp_shutdown(), "global shutdown")


class NativeInstance:
    def __init__(self, package: NativePackage, instance_id: Any) -> None:
        self.package = package
        self._instance_id = instance_id
        self._pointer = ctypes.cast(instance_id, ctypes.c_void_p)
        self.bytes = bytes(instance_id)

    def send(self, message: dict[str, Any]) -> None:
        payload = json.dumps(message, separators=(",", ":")).encode("utf-8")
        payload_buffer = ctypes.create_string_buffer(payload)
        status = self.package.library.magic_mcp_send(
            self._pointer,
            payload_buffer,
            len(payload),
        )
        self.package.check(status, "send MCP message")

    def receive(self, timeout_milliseconds: int = 5_000) -> dict[str, Any]:
        required = ctypes.c_size_t()
        status = self.package.library.magic_mcp_receive(
            self._pointer,
            None,
            0,
            ctypes.byref(required),
            timeout_milliseconds,
        )
        self.package.check(status, "receive size query", (BUFFER_TOO_SMALL,))

        buffer = ctypes.create_string_buffer(required.value)
        status = self.package.library.magic_mcp_receive(
            self._pointer,
            buffer,
            required.value,
            ctypes.byref(required),
            0,
        )
        self.package.check(status, "receive MCP message")
        return json.loads(bytes(buffer.raw[: required.value]))

    def request(self, request_id: int, method: str, parameters: dict[str, Any]) -> dict[str, Any]:
        self.send(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "method": method,
                "params": parameters,
            }
        )
        response = self.receive()
        if response.get("id") != request_id:
            raise RuntimeError(
                f"Expected response ID {request_id}, received {response.get('id')!r}."
            )
        if "error" in response:
            raise RuntimeError(f"MCP request {method!r} failed: {response['error']}")
        return response["result"]

    def initialize(self) -> None:
        result = self.request(
            1,
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {
                    "name": "magic-mcp-native-smoke",
                    "version": "1.0.0",
                },
            },
        )
        if result["serverInfo"]["name"] != "MagicAiGateway MCP Package Template":
            raise RuntimeError(f"Unexpected MCP server identity: {result['serverInfo']}")

        self.send(
            {
                "jsonrpc": "2.0",
                "method": "notifications/initialized",
                "params": {},
            }
        )

    def stop(self) -> None:
        status = self.package.library.magic_mcp_stop_instance(self._pointer)
        self.package.check(status, "stop instance")


def content_text(result: dict[str, Any]) -> str:
    return " ".join(
        block.get("text", "")
        for block in result.get("content", [])
        if block.get("type") == "text"
    )


def verify(path: Path) -> None:
    package = NativePackage(path)
    if package.library.magic_mcp_get_abi_version() != 1:
        raise RuntimeError("The package did not report ABI version 1.")

    manifest = package.manifest()
    if manifest["protocol"] != "magic-ai-gateway-mcp-package":
        raise RuntimeError(f"Unexpected package protocol: {manifest}")
    if manifest["name"] != "MagicAiGateway MCP Package Template":
        raise RuntimeError(f"Unexpected package manifest: {manifest}")

    first: NativeInstance | None = None
    second: NativeInstance | None = None

    try:
        first = package.start({"example": {"message": "first-instance"}})
        second = package.start({"example": {"message": "second-instance"}})

        live_ids = package.list_instances()
        if live_ids != {first.bytes, second.bytes}:
            raise RuntimeError(
                f"Expected two exact live instance IDs, received {len(live_ids)}."
            )

        first.initialize()
        second.initialize()

        tools_result = first.request(2, "tools/list", {})
        tool_names = {tool["name"] for tool in tools_result["tools"]}
        expected_tools = {"example_echo", "example_instance_status"}
        if not expected_tools.issubset(tool_names):
            raise RuntimeError(
                f"Generated MCP tools are missing: {expected_tools - tool_names}"
            )

        first_status = content_text(
            first.request(
                3,
                "tools/call",
                {"name": "example_instance_status", "arguments": {}},
            )
        )
        second_status = content_text(
            second.request(
                3,
                "tools/call",
                {"name": "example_instance_status", "arguments": {}},
            )
        )
        if "first-instance" not in first_status or "second-instance" not in second_status:
            raise RuntimeError(
                "Per-instance startup configuration was not isolated correctly."
            )

        first.stop()
        first = None
        if package.list_instances() != {second.bytes}:
            raise RuntimeError("Stopping one instance affected the live-instance registry incorrectly.")

        echo = content_text(
            second.request(
                4,
                "tools/call",
                {
                    "name": "example_echo",
                    "arguments": {"message": "still alive"},
                },
            )
        )
        if "still alive" not in echo:
            raise RuntimeError("The second instance stopped responding after the first was stopped.")

        second.stop()
        second = None
        if package.list_instances():
            raise RuntimeError("Stopped package instances remain in the live-instance registry.")
    finally:
        if first is not None:
            try:
                first.stop()
            except Exception:
                pass
        if second is not None:
            try:
                second.stop()
            except Exception:
                pass
        package.shutdown()

    print(f"Native MCP package smoke test passed: {path}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("library", type=Path)
    arguments = parser.parse_args()

    library = arguments.library.resolve()
    if not library.is_file():
        raise FileNotFoundError(library)

    verify(library)


if __name__ == "__main__":
    main()
