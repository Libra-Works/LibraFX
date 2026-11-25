---
title: Connect an MCP client
---

# Connect an MCP client

LIBERA FX exposes its active authoring graph through a local Model Context Protocol (MCP) server. An MCP client can inspect and edit the graph; this is separate from the engine's Live Preview connection. Keep the editor open with the document you intend to work on active.

## Connect safely

The default Streamable HTTP endpoint is:

```text
http://127.0.0.1:8765/mcp
```

The server binds to loopback only. Run the MCP client on the same computer as LIBERA FX; another machine or a remote/cloud client cannot reach this endpoint through the editor's network listener. Every request must include the `X-LiberaFx-Token` header. Browser-origin requests are blocked.

1. In LIBERA FX, choose **View → MCP connection...**. The **MCP Connection** panel displays the active **Streamable HTTP endpoint** and the `X-LiberaFx-Token`.
2. Select **Show token**, then use the adjacent **Copy** button. Treat the token like a password: do not paste it into a chat, source file, shared configuration, or log. A new editor launch creates a new token.
3. If you need a different port, open **Edit → Settings... → Servers**, change **MCP port**, then select **Save server settings**. Changes take effect after restarting LIBERA FX. Re-open **View → MCP connection...** and use the endpoint shown there.
4. The same panel contains the **AI operator skill** card. **Bundled SKILL.md** shows the included skill location; choose a local destination and select **Install / update** to copy or update it for a compatible agent. The skill is optional connection guidance, not a substitute for MCP authentication.

Japanese UI labels are **表示 → MCP 接続...**, **トークンを表示**, **コピー**, **編集 → 設定... → サーバー**, **MCP ポート**, **サーバー設定を保存**, **同梱 SKILL.md**, **AI 操作用スキル**, and **導入 / 更新**.

## Client configuration examples

Use the URL and token currently shown in the editor. Replace the default port if you changed it. Client config formats differ; the examples below use current Streamable HTTP entries and never include a real token.

### GitHub Copilot CLI or VS Code Copilot

For the Copilot CLI user configuration, use `$COPILOT_HOME/mcp-config.json` (or `~/.copilot/mcp-config.json` when `COPILOT_HOME` is unset). Copilot in VS Code also supports the portable `mcpServers` format through its MCP server configuration UI or supported configuration file:

```json
{
  "mcpServers": {
    "liberafx": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp",
      "headers": {
        "X-LiberaFx-Token": "${LIBERAFX_MCP_TOKEN}"
      }
    }
  }
}
```

Set `LIBERAFX_MCP_TOKEN` in the local environment used to launch the client, using the value copied from the editor. Do not commit a token or a user-specific secret-bearing config. See the [Copilot MCP configuration reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration) for configuration locations and current client options.

### Claude Code

A project-scoped `.mcp.json` can use an HTTP entry with a custom header:

```json
{
  "mcpServers": {
    "liberafx": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp",
      "headers": {
        "X-LiberaFx-Token": "REPLACE_WITH_TOKEN_LOCALLY"
      }
    }
  }
}
```

Replace the placeholder locally. Because the token changes when the editor restarts, keep this entry in an untracked local config or a user-scoped configuration; do not commit it to a shared project file. See [Claude Code's MCP guide](https://code.claude.com/docs/en/mcp) for its current configuration methods.

## Example conversations

These examples show the intent, not fixed node names or IDs. Ask the client to inspect the active graph and catalog first; it should use the exact node kinds, GUIDs and pin IDs returned by LIBERA FX.

### Make a simple effect graph

> **You:** Inspect the active graph and available node types. Propose a small timed pulse effect, and ask before replacing or removing anything. After I approve, create and connect the required nodes using the IDs and pins you found.
>
> **Client:** Reads the graph with `libera_graph_read`, checks available kinds with `libera_graph_list_node_types`, then uses `libera_graph_create_node` and `libera_graph_connect_nodes` for the approved edits. It reads the graph again to confirm the result.

### Add a reroute to a wire

> **You:** Add a reroute to the existing wire between these two nodes. Preserve other connections.
>
> **Client:** Uses `libera_graph_read` to identify the exact source/target GUIDs and port IDs, then calls `libera_graph_insert_reroute` and reads the graph again. This edit participates in the document's Undo history; `libera_graph_undo` can undo one edit.

### Tune a parameter during Live Preview

> **You:** A Live Preview client is connected. Find the node named “Pulse” and raise its existing intensity parameter to `0.75`. Confirm the node and parameter before changing it, then check whether the preview peer reports updated values.
>
> **Client:** Checks `libera_preview_clients`, reads node IDs and parameters with `libera_graph_read`, applies the requested value with `libera_graph_set_parameter`, and can inspect available runtime samples with `libera_preview_telemetry`.

Graph edits update the active authoring document. A connected, compatible engine preview may receive the change; the MCP server does not itself render the effect. Confirm the result in the engine preview and check its diagnostics. For engine setup, see [Connect and deliver](production-workflow.md); for panel locations, see [Windows and panels](editor-reference.md).
