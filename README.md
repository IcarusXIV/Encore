# Encore

**Your dance mods, one command away.** Create presets for your favourite dance and emote mods, then activate them instantly with a single click or custom chat command. Encore handles all the Penumbra priority juggling behind the scenes so you can just dance.

## Installation

Add this custom repo URL in Dalamud:
```
https://raw.githubusercontent.com/IcarusXIV/Encore/master/Encore/repo.json
```

Requires [Penumbra](https://github.com/xivdev/Penumbra).

## What Does It Do?

**Switch dances instantly** -- Pick a preset, click play (or type your command), and you're dancing. No more digging through Penumbra to swap priorities every time you want a different dance.

**Custom chat commands** -- Give each preset its own command like `/hips` or `/mydance`. Type it in chat and go.

**Pose presets** -- Not just emotes. Encore supports idle poses, sitting poses, ground sitting, and doze animations. It writes the pose index, triggers a redraw, and cycles `/cpose` automatically so you don't have to.

**Sit & doze anywhere** -- Chair-sit and doze presets place your character into the sit or doze state without needing furniture. No chair required.

**Conflict handling** -- When you activate a preset, Encore temporarily disables other mods that affect the same emote so yours always wins. Pin your important mods to protect them from being disabled.

**Mod option switching** -- If your mod has option groups (music, ear wiggles, different animations, etc.), you can configure which options to apply per preset. They get restored when you switch away.

**Vanilla presets** -- Want to go back to the original game animation? Create a vanilla preset that just disables conflicting mods without enabling anything.

**Warp to target** -- For duo or group emotes, use `/warp` (or the button in the main window) to warp to your partner's exact position. Stand next to them first -- it only works within a short distance.

## Organising Your Presets

- **Folders** -- Group presets into collapsible, colour-coded folders
- **Drag & drop** -- Reorder presets and move them between folders
- **Sort modes** -- Custom, Name, Command, Favourites, Newest, Oldest
- **Search** -- Filter presets by name
- **Favourites** -- Star your go-to presets for quick access
- **Icons** -- Pick from hundreds of game icons to make each preset recognizable at a glance

## Commands

| Command | What it does |
|---------|-------------|
| `/encore` | Open the main window |
| `/encorereset` | Restore all mods to their original state |
| `/warp` | Warp to your target's position (must be close) |
| `/yourcommand` | Activate a preset (you define these) |

## Getting Started

1. Open Encore with `/encore`
2. Click **New Preset**
3. Give it a name, a chat command, and an icon
4. Pick a dance mod (populated from your Penumbra Library)
5. Choose which base dance (if multiple options) & optionally set mod options
6. Hit **Save** and you're done -- click play or type your command to activate it

Everything is restored when you switch to a different preset or use `/encorereset`.

## Support

- **Discord:** [Join the community](https://discord.gg/8JykGErcX4)
- **Ko-fi:** [Support development](https://ko-fi.com/icarusxiv)
- **Issues:** [Report bugs on GitHub](https://github.com/IcarusXIV/Encore/issues)

## Credits

Created by **Icarus**