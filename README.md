# The Stalker

A knife-only boss for SPT 4.1.x: he stalks you in the dark, kills the lights around him and strikes when you're
not looking. Labs (with Blackout Addons) and every other map at night.

- `Client/` - BepInEx plugin (`com.blackoutaddons.thestalker`), net472
- `Server/` - SPT server mod (bot type, loot, spawn waves), net10.0
- `Prepatch/` - BepInEx patcher that adds his WildSpawnType value
- `audio/` - Python scripts used to make his sounds (stab stings, the processed laugh recordings, PA lines)
  (for reference: they rely on shared DSP helpers and Piper voice models that aren't included; the finished sounds ship in the mod zip)

## Building

Client: put the game's `EscapeFromTarkov_Data/Managed` DLLs plus `BepInEx.dll` and `0Harmony.dll` where the csproj
points, and `DrakiaXYZ-BigBrain.dll`, `MoreBotsPlugin.dll`, `MoreBotsPrepatch.dll` in `libs/` (not included - get them
from their authors). Then `dotnet build -c Release`.

Server: `dotnet build -c Release` (SPTarkov.Server.Core from NuGet).

## How this was made

Designed, directed and play-tested by the mod's author; the code was written with the help of an AI assistant
(Claude, by Anthropic).

## License

MIT (see LICENSE). Not affiliated with Battlestate Games, SPT, Vultify, WTT, or the authors of SAIN, BigBrain or MoreBotsAPI.
