# ThunderRoadMcp

A local MCP server and in-game bridge for Blade & Sorcery **PC Mono/JIT**. It lets a coding agent inspect game logs and installed assemblies, browse catalog data, check or launch the game, install the bridge, and, when the bridge is enabled, compile and execute C# inside the game process.

It does not use Harmony, private-method patching, mod replacement, hot reload, or state-preserving reload.

## Security

`evaluate_csharp` executes trusted arbitrary C# inside the game with full process, game API, and filesystem access. It is not sandboxed. Already-running code cannot be forcibly interrupted and may block the game. The installer enables the bridge when it installs the bridge mod; installing the mod by any other route leaves it offline. The listener is loopback-only (`127.0.0.1`) and authenticated with a private 32-byte random token. Never expose the listener or share or check in the token.

## Requirements

- Windows with Windows PowerShell 5.1.
- .NET 10 runtime (`Microsoft.NETCore.App` 10.x) to run the published host. The published `ThunderRoadMcp.Host.exe` is a framework-dependent apphost, not self-contained.
- .NET 10 SDK only for source checkout installs and local bridge builds.
- Blade & Sorcery installed through Steam or Meta Horizon Link to install and use the bridge. Without the game, the installer can install the host only.

## Quick start

Download and extract a release ZIP from the repository's GitHub releases. In the extracted folder:

```powershell
.\scripts\install.ps1
```

Release installs need only the .NET 10 runtime. They use the prebuilt host and, when the game is found, the prebuilt bridge. If no game is found, or if `-SkipBridge` is passed, the installer installs the host only.

When the game is found, the installer also enables the bridge by creating `%LOCALAPPDATA%\ThunderRoadMcp\bridge.json`: an enabled loopback config with a freshly generated private 32-byte token. The token is never printed. Pass `-NoBridgeConfig` to install the bridge mod but create no config, leaving the bridge disabled; pass `-SkipBridge` for a host-only install. An existing `bridge.json` is never overwritten, enabled, or disabled. See [Security](#security) and [bridge configuration](docs/usage.md#bridge-configuration).

Source checkout: run the same command from the repository root. It requires the .NET 10 SDK and builds the host and, when the game is found, the bridge. Game selection, custom paths, update, uninstall, and troubleshooting are in [Installation](docs/installation.md).

### Connect an agent

The installer prints a generic MCP stdio configuration and saves it as `mcp-config.json` in the host install directory. Add it to your harness's own MCP configuration location and format. Use the absolute installed EXE as `command` with empty `args`:

```json
{
  "mcpServers": {
    "ThunderRoadMcp": {
      "command": "C:/Users/<you>/AppData/Local/ThunderRoadMcp/host/ThunderRoadMcp.Host.exe",
      "args": []
    }
  }
}
```

If `THUNDERROAD_MCP_CONFIG` or `THUNDERROAD_MCP_LAUNCH_CONFIG` is set during installation, the generated configuration includes those environment overrides. The bridge config path must also be available to the game. See [generated stdio configuration](docs/installation.md#generated-stdio-configuration) for details.

Restart or reconnect the agent after changing its MCP configuration. Check `game_installations` and `game_status` first, then use the offline tools. `game_guide` returns the embedded game guide by section (default `bootstrap`) and works without the game or bridge. See [Usage](docs/usage.md) for workflows and readiness checks.

## Documentation

- [Building and testing](docs/building.md)
- [Installation](docs/installation.md)
- [Usage](docs/usage.md)
- [Blade & Sorcery game guide](docs/game-guide.md)
- [Agent skill for Blade & Sorcery](skills/blade-and-sorcery/SKILL.md)
- [API and catalog discovery](docs/api-discovery.md)
- [Bridge protocol](docs/protocol.md)
