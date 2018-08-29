# Beat Saber HTTP Status

This plugin exposes information about the current game status, live over a WebSocket and over HTTP. It can be used to build [custom stream overlays](https://github.com/opl-/beatsaber-http-status/wiki/Software-using-this-plugin#overlays) or track player performance by third party programs.

## Installation

### Manual

1. Download the latest release from the [releases page](https://github.com/opl-/beatsaber-http-status/releases).

2. Extract the zip into your Beat Saber directory.

3. [Get additional software](https://github.com/opl-/beatsaber-http-status/wiki/Software-using-this-plugin) that makes use of this plugin.

## Developers

Protocol documentation can be found in [protocol.md](protocol.md).

To build this project you have to provide some game dlls in the `libs/beatsaber` directory. For a full list see the [project file](BeatSaberHTTPStatus/BeatSaberHTTPStatusPlugin.csproj). This project also uses the `websocket-sharp` library included as a git submodule. To download it, use `git submodule update --init`.

## Credits

**xyonico** for the [Beat Saber Discord Presence](https://github.com/xyonico/BeatSaberDiscordPresence) plugin, on which this plugin was initially based.

**sta** for the [websocket-sharp](https://github.com/sta/websocket-sharp) library.

**Maxaxik** for testing and helping with research.
