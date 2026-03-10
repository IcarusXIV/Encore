# Encore

**Your dance mods, one command away.** Create presets for your favourite dance and emote mods, then activate them instantly with a single click or custom chat command. Encore handles all the Penumbra priority juggling behind the scenes so you can just dance.

## Installation

Add this custom repo URL in Dalamud:
```
https://raw.githubusercontent.com/IcarusXIV/Encore/master/Encore/repo.json
```

Requires [Penumbra](https://github.com/xivdev/Penumbra).

## What Can It Do?

**Switch dances instantly** -- Pick a preset, click play (or type your command), and you're dancing. No more digging through Penumbra to swap priorities every time you want a different dance.

**Custom chat commands** -- Give each preset its own command like `/hips` or `/mydance`. Type it in chat and go.

**Preset modifiers** -- One preset, multiple variants. Add modifiers that override specific mod options or the emote command. Trigger them by adding a word after the chat command (`/jj slow`, `/jj fast`) or from the context menu.

**Emote unlock bypass** -- Don't have the emote? No problem. With "Allow All Emotes" enabled in settings, your dance and emote mod presets work regardless of whether you have the base emote or not. Just check "I don't have this emote" when creating a preset. You can also use `/vanilla <emote>` for a quick one-off. This automatically creates a Penumbra mod called _EncoreEmoteSwap in your mod directory -- this is normal and required for the bypass to work. Like other mods through sync plugins, the animation may not be visible to others on the first play -- just give it a second to load, then do the emote again

**Vanilla emotes** -- Want the original game animation? Type `/vanilla <emote>` to temporarily disable all mods for that emote and play it unmodded. Or create a vanilla preset for one-click access.

**Pose presets** -- Not just emotes. Encore supports idle poses, sitting poses, ground sitting, and doze animations. It writes the pose index, triggers a redraw, and cycles `/cpose` automatically so you don't have to.

**Movement mods** -- Walking, sprinting, and jogging animation mods are detected and supported. Just enable and go -- no emote execution needed.

**Sit & doze anywhere** -- Opt-in setting that lets chair-sit and doze presets place your character into the sit or doze state without needing furniture. Enable it in the settings popup (gear icon).

**Conflict handling** -- When you activate a preset, Encore temporarily disables other mods that affect the same emote so yours always wins. Pin your important mods to protect them from being disabled.

**Mod option switching** -- If your mod has option groups (outfits, variants, etc.), you can configure which options to apply per preset. They get restored when you switch away.

**Emote looping** -- `/loop <emote>` continuously repeats any non-looping emote until you move or type `/loop` again.

**Align to target** -- For duo or group emotes, use `/align` (or the button in the main window) to walk to your partner's exact position. Stand close first -- it only works within a short distance.

## Organising Your Presets

- **Folders** -- Group presets into collapsible, colour-coded folders
- **Drag & drop** -- Reorder presets and move them between folders
- **Sort modes** -- Custom, Name, Command, Favourites, Newest, Oldest
- **Search** -- Filter presets by name
- **Favourites** -- Star your go-to presets for quick access
- **Icons** -- Pick from hundreds of game icons, or upload your own custom images

## Commands

| Command | What it does |
|---------|-------------|
| `/encore` | Open the main window |
| `/encorereset` | Restore all mods to their original state |
| `/align` | Walk to your target's position (must be close) |
| `/loop <emote>` | Loop a non-looping emote. `/loop` alone stops it |
| `/vanilla <emote>` | Play an emote with all mods disabled |
| `/yourcommand` | Activate a preset (you define these) |
| `/yourcommand modifier` | Activate a preset modifier variant |

## Getting Started

1. Open Encore with `/encore`
2. Click **New Preset** and pick a dance mod from your Penumbra library
3. Give it a name, a chat command, and an icon
4. Hit **Save** and you're done -- click play or type your command to activate it

Everything is restored when you switch to a different preset or use `/encorereset`.

## Support

- **Discord:** [Join the community](https://discord.gg/8JykGErcX4)
- **Ko-fi:** [Support development](https://ko-fi.com/icarusxiv)
- **Issues:** [Report bugs on GitHub](https://github.com/IcarusXIV/Encore/issues)

## Credits

Created by **Icarus**
