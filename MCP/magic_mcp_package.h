#ifndef MAGIC_MCP_PACKAGE_H
#define MAGIC_MCP_PACKAGE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define MAGIC_MCP_CALL __cdecl
#else
#define MAGIC_MCP_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define MAGIC_MCP_ABI_VERSION 1
#define MAGIC_MCP_INSTANCE_ID_SIZE 16

typedef enum magic_mcp_status {
    MAGIC_MCP_SUCCESS = 0,
    MAGIC_MCP_NO_MESSAGE = 1,
    MAGIC_MCP_INVALID_ARGUMENT = 2,
    MAGIC_MCP_INSTANCE_NOT_FOUND = 3,
    MAGIC_MCP_BUFFER_TOO_SMALL = 4,
    MAGIC_MCP_INSTANCE_STOPPED = 5,
    MAGIC_MCP_INTERNAL_ERROR = 100
} magic_mcp_status;

/* Returns the package ABI version directly, not a magic_mcp_status value. */
int32_t MAGIC_MCP_CALL magic_mcp_get_abi_version(void);

/*
 * Writes the UTF-8 package manifest. Use the normal query-size pattern:
 * call with output == NULL and output_capacity == 0, allocate output_length
 * bytes, then call again.
 */
int32_t MAGIC_MCP_CALL magic_mcp_get_manifest(
    uint8_t* output,
    size_t output_capacity,
    size_t* output_length);

/*
 * Starts one independent package instance. configuration_json may be NULL only
 * when configuration_length is zero; otherwise it must contain one UTF-8 JSON
 * object. instance_id_output must point to MAGIC_MCP_INSTANCE_ID_SIZE writable
 * bytes. The ID is opaque and must be returned unchanged on later calls.
 */
int32_t MAGIC_MCP_CALL magic_mcp_start_instance(
    const uint8_t* configuration_json,
    size_t configuration_length,
    uint8_t instance_id_output[MAGIC_MCP_INSTANCE_ID_SIZE]);

/* Sends exactly one UTF-8 MCP JSON-RPC object to one instance. */
int32_t MAGIC_MCP_CALL magic_mcp_send(
    const uint8_t instance_id[MAGIC_MCP_INSTANCE_ID_SIZE],
    const uint8_t* message,
    size_t message_length);

/*
 * Receives exactly one UTF-8 MCP JSON-RPC object from one instance.
 * timeout_milliseconds: -1 waits indefinitely, 0 polls, positive values wait
 * that many milliseconds. On MAGIC_MCP_BUFFER_TOO_SMALL, output_length contains
 * the required size and the message remains pending for the next receive call.
 */
int32_t MAGIC_MCP_CALL magic_mcp_receive(
    const uint8_t instance_id[MAGIC_MCP_INSTANCE_ID_SIZE],
    uint8_t* output,
    size_t output_capacity,
    size_t* output_length,
    int32_t timeout_milliseconds);

/* Stops and destroys one package instance. */
int32_t MAGIC_MCP_CALL magic_mcp_stop_instance(
    const uint8_t instance_id[MAGIC_MCP_INSTANCE_ID_SIZE]);

/*
 * Writes all live opaque IDs as a packed sequence of 16-byte values. The byte
 * length is always a multiple of MAGIC_MCP_INSTANCE_ID_SIZE.
 */
int32_t MAGIC_MCP_CALL magic_mcp_list_instances(
    uint8_t* output,
    size_t output_capacity,
    size_t* output_length);

/* Stops every remaining instance. Hosts should call this before process exit. */
int32_t MAGIC_MCP_CALL magic_mcp_shutdown(void);

/*
 * Writes the UTF-8 diagnostic text associated with the most recent ABI call on
 * the current calling thread. This is diagnostic only; branch on status codes.
 */
int32_t MAGIC_MCP_CALL magic_mcp_get_last_error(
    uint8_t* output,
    size_t output_capacity,
    size_t* output_length);

#ifdef __cplusplus
}
#endif

#endif /* MAGIC_MCP_PACKAGE_H */
