# Encore

**Your Dance Mods, One Command Away** — Create presets for your favorite dance and emote mods, then activate them instantly with custom chat commands. Automatically handles Penumbra mod priorities so your animations always show correctly.

## Installation

Add this repository to Dalamud:
```
https://raw.githubusercontent.com/IcarusXIV/Encore/master/repo.json
```

## What It Does

- **One-Click Dance Switching** — Save dance/emote mods as presets and activate them instantly
- **Custom Chat Commands** — Create your own commands like `/hips` or `/mydance` for each preset
- **Automatic Priority Management** — Boosts your target mod's priority so it always wins over conflicts
- **Smart Emote Detection** — Automatically detects which emote a mod affects and executes it
- **Icon Picker** — Choose from hundreds of game icons for your presets
- **Pose Support** — Works with idle poses, sitting poses (chair/groundsit), and doze animations (triggers redraw automatically)

## Features

**Preset Management:**
- Create unlimited presets for dance/emote mods
- Favourite and organize your presets
- Duplicate presets for variations
- Custom icons from FFXIV's icon library

**Smart Detection:**
- Scans your Penumbra mods to find emote/dance mods
- Identifies the correct emote command automatically
- Distinguishes between emotes and pose replacements
- Handles all emote variants (cheer, ranger poses, etc.)

**Seamless Integration:**
- Works with your existing Penumbra setup
- Respects Dalamud's UI scaling
- Validates commands to prevent conflicts with game/plugin commands
- Remembers original mod priorities for clean deactivation

## Commands

| Command | Description |
|---------|-------------|
| `/encore` | Open the main Encore window |
| `/[yourpreset]` | Activate a preset (custom command you define) |

## How It Works

1. **Create a Preset** — Click the + button and select a dance/emote mod from your Penumbra library
2. **Set a Command** — Give it a memorable chat command like `/hips`
3. **Pick an Icon** — Choose an icon that represents your dance
4. **Use It!** — Type your command in chat and watch the magic happen

Encore automatically:
- Boosts your mod's priority so it takes precedence
- Executes the correct emote command
- Triggers a redraw if needed (for pose mods)

## Requirements

- [Penumbra](https://github.com/xivdev/Penumbra) — Required for mod management

## Support

- **Discord:** [Join the community](https://discord.gg/8JykGErcX4)
- **Ko-fi:** [Support development](https://ko-fi.com/icarusxiv)
- **Issues:** [Report bugs on GitHub](https://github.com/IcarusXIV/Encore/issues)

## Credits

Created by **Icarus**

Inspired by the workflow needs of dancers, GPosers, and emote mod enthusiasts everywhere.
