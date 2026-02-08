using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Encore.Services;

// JSON models for parsing Penumbra mod files
internal class ModOption
{
    [JsonPropertyName("Files")]
    public Dictionary<string, string>? Files { get; set; }
}

internal class ModGroup
{
    [JsonPropertyName("Options")]
    public List<ModOption>? Options { get; set; }
}

/// <summary>
/// Service for detecting emote-related mods and their properties.
/// Uses the same categorisation logic and caching pattern as Character Select+.
/// </summary>
public class EmoteDetectionService
{
    private readonly PenumbraService penumbraService;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    // Cache for emote mod detection
    private readonly EmoteModCache cache;
    private bool isInitializing;
    private int scanGeneration;  // Incremented each time we start a new scan

    public bool IsInitialized => cache.IsInitialized;
    public bool IsInitializing => isInitializing;

    public int GetCachedModCount() => cache.GetCachedModCount();

    // FFXIV emote_sp special emote IDs -> emote names
    // These map sp## filenames (from bt_common/emote_sp/) to their EmoteToCommand keys
    // The emote_sp folder contains "special" emotes that use sp## naming instead of descriptive names
    private static readonly Dictionary<string, string> SpToEmote = new(StringComparer.OrdinalIgnoreCase)
    {
        { "sp41", "showleft" },
        { "sp42", "showright" },
    };

    // FFXIV's actual emote commands - comprehensive mapping from file names to slash commands
    // Based on FFXIV game data: ActionTimeline sheet, emote file paths, and game data mining
    // Format: file name (without extension) -> slash command
    private static readonly Dictionary<string, string> EmoteToCommand = new(StringComparer.OrdinalIgnoreCase)
    {
        // ============================================
        // DANCE EMOTES (Primary focus for this plugin)
        // ============================================
        // Base dance - /dance
        // NOTE: We only map "dance" directly, NOT race/gender variants like dance_female, dance_male
        // Those variants are ambiguous - they could be used by ANY dance emote (base, harvest, step, etc.)
        // When we see dance_female_loop.pap, we let GetChangedItems tell us which emote it actually affects
        { "dance", "/dance" },

        // Step Dance - /stepdance (dance02)
        { "dance02", "/stepdance" },
        { "stepdance", "/stepdance" },
        { "step dance", "/stepdance" },  // Penumbra returns with space
        { "step", "/stepdance" },
        { "sdance", "/stepdance" },

        // Harvest Dance - /harvestdance (dance03)
        { "dance03", "/harvestdance" },
        { "harvestdance", "/harvestdance" },
        { "harvest dance", "/harvestdance" },  // Penumbra returns with space
        { "harvest", "/harvestdance" },
        { "hdance", "/harvestdance" },

        // Gold Dance - /golddance (dance04)
        { "dance04", "/golddance" },
        { "golddance", "/golddance" },
        { "gold dance", "/golddance" },  // Penumbra returns with space
        { "gdance", "/golddance" },
        { "gold", "/golddance" },

        // Numbered dances (dance05-dance21) - FFXIV internal animation IDs
        // These map to specific emotes based on when they were added to the game
        { "dance05", "/balldance" },
        { "dance06", "/mandervilledance" },
        { "dance07", "/bombdance" },
        { "dance08", "/mogdance" },
        { "dance09", "/songbird" },
        { "dance10", "/thavdance" },
        { "dance11", "/easterndance" },
        { "dance12", "/sundance" },
        { "dance13", "/moonlift" },
        { "dance14", "/popotostep" },
        { "dance15", "/sidestep" },
        { "dance16", "/beesknees" },
        { "dance17", "/flamedance" },
        { "dance18", "/yoldance" },
        { "dance19", "/littleladiesdance" },
        { "dance20", "/heeltoe" },
        { "dance21", "/mandervillemambo" },

        // Ball Dance - /balldance
        { "balldance", "/balldance" },
        { "ball dance", "/balldance" },  // Penumbra returns with space
        { "ball", "/balldance" },

        // Manderville Dance - /mandervilledance
        { "manderville", "/mandervilledance" },
        { "mandervilledance", "/mandervilledance" },
        { "manderville dance", "/mandervilledance" },

        // Manderville Mambo - /mandervillemambo
        { "mmambo", "/mandervillemambo" },
        { "mandervillemambo", "/mandervillemambo" },
        { "manderville mambo", "/mandervillemambo" },
        { "mambo", "/mandervillemambo" },

        // Bomb Dance - /bombdance
        { "bombdance", "/bombdance" },
        { "bomb dance", "/bombdance" },  // Penumbra returns with space
        { "bomb", "/bombdance" },

        // Moogle Dance - /mogdance
        { "mogdance", "/mogdance" },
        { "mog dance", "/mogdance" },  // Penumbra returns with space
        { "moogledance", "/mogdance" },
        { "moogle dance", "/mogdance" },  // Penumbra returns with space
        { "moogle", "/mogdance" },

        // Songbird - /songbird
        { "songbird", "/songbird" },

        // Thavnairian Dance - /thavdance or /tdance
        { "thavdance", "/thavdance" },
        { "thav dance", "/thavdance" },  // Penumbra returns with space
        { "thavnairian dance", "/thavdance" },  // Penumbra returns with space
        { "thav", "/thavdance" },
        { "tdance", "/thavdance" },
        { "thavnairiandance", "/thavdance" },

        // Eastern Dance - /easterndance
        { "edance", "/easterndance" },
        { "eastern dance", "/easterndance" },  // Penumbra returns with space
        { "eastern", "/easterndance" },
        { "easterndance", "/easterndance" },

        // Bee's Knees - /beesknees
        { "beesknees", "/beesknees" },
        { "bees knees", "/beesknees" },  // Penumbra returns with space
        { "bee's knees", "/beesknees" },  // Penumbra returns with apostrophe
        { "bees", "/beesknees" },
        { "bees_knees", "/beesknees" },
        { "bee_s_knees", "/beesknees" },
        { "beesknee", "/beesknees" },

        // Side Step - /sidestep
        { "sidestep", "/sidestep" },
        { "side step", "/sidestep" },  // Penumbra returns with space
        { "side", "/sidestep" },

        // Sundrop Dance - /sundance
        { "sundrop", "/sundance" },
        { "sundropdance", "/sundance" },
        { "sundrop dance", "/sundance" },  // Penumbra returns with space
        { "sundance", "/sundance" },

        // Moonlift Dance - /moonlift (NOT "moondrop" - that doesn't exist)
        { "moonlift", "/moonlift" },
        { "moondrop", "/moonlift" },  // Common misnomer, redirect to moonlift
        { "moondropdance", "/moonlift" },  // Common misnomer, redirect to moonlift

        // Popoto Step - /popotostep
        { "popotostep", "/popotostep" },
        { "popoto step", "/popotostep" },  // Penumbra returns with space
        { "popoto", "/popotostep" },

        // Flame Dance - /flamedance
        { "flamedance", "/flamedance" },
        { "flame dance", "/flamedance" },  // Penumbra returns with space
        { "flame", "/flamedance" },

        // Yol Dance - /yoldance
        { "yoldance", "/yoldance" },
        { "yol dance", "/yoldance" },  // Penumbra returns with space
        { "yol", "/yoldance" },

        // Little Ladies' Dance - /littleladiesdance
        { "ladance", "/littleladiesdance" },
        { "la dance", "/littleladiesdance" },  // Penumbra returns with space
        { "littleladiesdance", "/littleladiesdance" },
        { "little ladies dance", "/littleladiesdance" },  // Penumbra returns with space
        { "little ladies' dance", "/littleladiesdance" },

        // Heel Toe - /heeltoe
        { "heeltoe", "/heeltoe" },
        { "heel toe", "/heeltoe" },  // Penumbra returns with space

        // Lali-hop - /lalihop
        { "lalihop", "/lalihop" },

        // Lophop - /lophop
        { "lophop", "/lophop" },

        // ============================================
        // EXPRESSION EMOTES
        // ============================================
        // Joy - /joy
        { "joy", "/joy" },

        // Cheer - /cheer
        { "cheer", "/cheer" },

        // Hum - /hum
        { "hum", "/hum" },

        // Wave - /wave
        { "wave", "/wave" },

        // Goodbye - /goodbye
        { "goodbye", "/goodbye" },

        // Beckon - /beckon (comeon in files)
        { "beckon", "/beckon" },
        { "comeon", "/beckon" },

        // Bow - /bow
        { "bow", "/bow" },

        // Eastern Bow - /easternbow or /ebow
        { "easternbow", "/easternbow" },
        { "ebow", "/easternbow" },

        // Kneel - /kneel
        { "kneel", "/kneel" },

        // Clap - /clap
        { "clap", "/clap" },

        // Think - /think
        { "think", "/think" },

        // Ponder - /ponder (make_act in files)
        { "ponder", "/ponder" },
        { "make_act", "/ponder" },

        // Stretch - /stretch
        { "stretch", "/stretch" },

        // Eastern Stretch - /easternstretch
        { "estretch", "/easternstretch" },
        { "easternstretch", "/easternstretch" },
        { "eastern stretch", "/easternstretch" },

        // Flex - /flex (posing in files)
        { "flex", "/flex" },
        { "posing", "/flex" },

        // Victory Pose - /vpose
        { "vpose", "/vpose" },
        { "victory", "/vpose" },

        // Battle Stance - /battlestance
        { "bstance", "/battlestance" },
        { "battlestance", "/battlestance" },
        { "battle stance", "/battlestance" },
        { "battle01", "/battlestance" },

        // Change Pose - /cpose
        { "cpose", "/cpose" },
        { "changepose", "/cpose" },

        // Hildy - /hildy (Hildibrand pose)
        { "hildy", "/hildy" },

        // Upset - /upset
        { "upset", "/upset" },

        // Angry - /angry
        { "angry", "/angry" },

        // Cry - /cry
        { "cry", "/cry" },

        // Laugh - /laugh
        { "laugh", "/laugh" },

        // Fume - /fume
        { "fume", "/fume" },

        // Panic - /panic
        { "panic", "/panic" },

        // Grovel - /grovel (orz/dogeza in files)
        { "grovel", "/grovel" },
        { "orz", "/grovel" },
        { "dogeza", "/grovel" },

        // Blush - /blush
        { "blush", "/blush" },

        // Psych - /psych
        { "psych", "/psych" },

        // Chuckle - /chuckle
        { "chuckle", "/chuckle" },

        // Huh - /huh
        { "huh", "/huh" },

        // Hurray - /hurray
        { "hurray", "/hurray" },
        { "huzzah", "/hurray" },

        // Fist Pump - /fistpump
        { "fistpump", "/fistpump" },

        // Air Quotes - /airquotes
        { "airquotes", "/airquotes" },
        { "airquote", "/airquotes" },

        // Amazed - /amazed
        { "amazed", "/amazed" },

        // Surprised - /surprised
        { "surprised", "/surprised" },

        // Deny - /deny
        { "deny", "/deny" },

        // Doubt - /doubt
        { "doubt", "/doubt" },

        // Examine - /examineself
        { "examine", "/examineself" },
        { "examineself", "/examineself" },

        // Furious - /furious
        { "furious", "/furious" },

        // Lookout - /lookout
        { "lookout", "/lookout" },

        // Me - /me
        { "me", "/me" },

        // No - /no
        { "no", "/no" },

        // Yes - /yes
        { "yes", "/yes" },

        // Point - /point
        { "point", "/point" },

        // Salute - /salute
        { "salute", "/salute" },
        { "salute_gca", "/flamesalute" },
        { "flamesalute", "/flamesalute" },
        { "flame salute", "/flamesalute" },
        { "salute_gcb", "/serpentsalute" },
        { "serpentsalute", "/serpentsalute" },
        { "serpent salute", "/serpentsalute" },
        { "salute_gcc", "/stormsalute" },
        { "stormsalute", "/stormsalute" },
        { "storm salute", "/stormsalute" },

        // Shocked - /shocked
        { "shocked", "/shocked" },

        // Shrug - /shrug
        { "shrug", "/shrug" },

        // Slap - /slap
        { "slap", "/slap" },

        // Sulk - /sulk
        { "sulk", "/sulk" },

        // Welcome - /welcome
        { "welcome", "/welcome" },

        // Wink - /wink
        { "wink", "/wink" },
        { "leftwink", "/wink" },
        { "rightwink", "/wink" },

        // Thumbs Up - /thumbsup
        { "thumbsup", "/thumbsup" },

        // Comfort - /comfort
        { "comfort", "/comfort" },

        // Congratulate - /congratulate (praise in files)
        { "congratulate", "/congratulate" },
        { "praise", "/congratulate" },

        // Hug - /hug
        { "hug", "/hug" },

        // Blow Kiss - /blowkiss
        { "blowkiss", "/blowkiss" },

        // Pet - /pet (stroke in files)
        { "pet", "/pet" },
        { "stroke", "/pet" },

        // Poke - /poke
        { "poke", "/poke" },

        // Pray - /pray
        { "pray", "/pray" },
        { "ritualpray", "/pray" },

        // Soothe - /soothe
        { "soothe", "/soothe" },

        // ============================================
        // SITTING / RESTING EMOTES
        // ============================================
        // Doze - /doze
        { "doze", "/doze" },

        // Sit (chair) - /sit
        { "sit", "/sit" },

        // Ground Sit - /groundsit
        { "groundsit", "/groundsit" },
        { "jmn", "/groundsit" },

        // Lean - /lean
        { "lean", "/lean" },

        // Play Dead - /playdead
        { "playdead", "/playdead" },
        { "pdead", "/playdead" },

        // ============================================
        // ACTION EMOTES
        // ============================================
        // Box - /box
        { "box", "/box" },

        // Box Step - /boxstep
        { "boxstep", "/boxstep" },
        { "box step", "/boxstep" },

        // Throw - /throw
        { "throw", "/throw" },
        { "throw_snow", "/throw" },

        // Tomestone - /tomestone
        { "tomestone", "/tomestone" },

        // Rally - /rally
        { "rally", "/rally" },

        // Pretty Please - /prettyplease
        { "prettyplease", "/prettyplease" },
        { "pplease", "/prettyplease" },

        // Straight Face - /straightface
        { "straightface", "/straightface" },

        // Attention - /attention
        { "attention", "/attention" },

        // Aback - /aback
        { "aback", "/aback" },

        // Beam - /beam
        { "beam", "/beam" },

        // Malevolence - /malevolence
        { "malevolence", "/malevolence" },

        // Stagger - /stagger
        { "stagger", "/stagger" },

        // Gratuity - /gratuity
        { "gratuity", "/gratuity" },

        // Reprimand - /reprimand
        { "reprimand", "/reprimand" },

        // Eastern Greeting - /easterngreeting
        { "easterngreeting", "/easterngreeting" },
        { "egreet", "/easterngreeting" },

        // Endure - /endure
        { "endure", "/endure" },

        // Converse - /converse
        { "converse", "/converse" },

        // Snap - /snap
        { "snap", "/snap" },

        // Taunt - /taunt
        { "taunt", "/taunt" },

        // Reflect - /reflect
        { "reflect", "/reflect" },

        // Respect - /respect
        { "respect", "/respect" },

        // Alert - /alert
        { "alert", "/alert" },

        // Scheme - /scheme
        { "scheme", "/scheme" },

        // Sweat - /sweat
        { "sweat", "/sweat" },

        // Determined - /determined
        { "determined", "/determined" },

        // Embrace - /embrace
        { "embrace", "/embrace" },

        // Face Palm - /facepalm
        { "facepalm", "/facepalm" },

        // Furrow - /furrow
        { "furrow", "/furrow" },

        // Headache - /headache
        { "headache", "/headache" },

        // Observe - /observe
        { "observe", "/observe" },

        // Clutch Head - /clutchhead
        { "clutch", "/clutchhead" },
        { "clutchhead", "/clutchhead" },

        // Confirm - /confirm
        { "confirm", "/confirm" },

        // Reject - /reject
        { "reject", "/reject" },

        // Dote - /dote
        { "dote", "/dote" },

        // Water Float - /waterfloat
        { "waterfloat", "/waterfloat" },
        { "water", "/waterfloat" },

        // Splash - /splash
        { "splash", "/splash" },

        // Read - /read
        { "read", "/read" },

        // Toast - /toast
        { "toast", "/toast" },

        // Limber Up - /limberup
        { "limberup", "/limberup" },

        // Push Ups - /pushups
        { "pushups", "/pushups" },

        // Sit Ups - /situps
        { "situps", "/situps" },

        // Squats - /squats
        { "squats", "/squats" },

        // Guard - /guard
        { "guard", "/guard" },

        // Backflip - /backflip
        { "backflip", "/backflip" },
        { "bflip", "/backflip" },

        // Twirl - /twirl
        { "twirl", "/twirl" },

        // Happy - /happy
        { "happy", "/happy" },

        // Annoy - /annoy (was missing)
        { "annoy", "/annoy" },

        // ============================================
        // SPECIAL / EVENT EMOTES
        // ============================================
        // Change Step (Dancer) - /changestep
        { "changestep", "/changestep" },

        // Gentleman Pose - /gentlemanpose
        { "gentlemanpose", "/gentlemanpose" },

        // Power Up - /powerup
        { "powerup", "/powerup" },

        // Eureka - /eureka
        { "eureka", "/eureka" },

        // Lali-ho - /laliho
        { "laliho", "/laliho" },

        // Get Fantasy - /getfantasy
        { "getfantasy", "/getfantasy" },

        // Visor - /visor
        { "visor", "/visor" },

        // Sheathe - /sheathe
        { "sheathe", "/sheathe" },

        // Magic Trick - /magictrick
        { "magictrick", "/magictrick" },

        // Photograph - /photograph
        { "photograph", "/photograph" },

        // High Five - /highfive
        { "highfive", "/highfive" },
        { "hfive", "/highfive" },

        // Fist Bump - /fistbump
        { "fistbump", "/fistbump" },

        // Love Heart - /loveheart
        { "loveheart", "/loveheart" },
        { "heart", "/loveheart" },

        // Flower Shower - /flowershower
        { "flowershower", "/flowershower" },
        { "petals", "/flowershower" },

        // Bouquet - /bouquet
        { "bouquet", "/bouquet" },

        // Sabotender - /sabotender
        { "sabotender", "/sabotender" },

        // Goobue Do - /goobbuedo
        { "goobbuedo", "/goobbuedo" },
        { "mysterymachine", "/goobbuedo" },

        // Wasshoi - /wasshoi
        { "wasshoi", "/wasshoi" },
        { "uchiwasshoi", "/wasshoi" },
        { "bigfan", "/wasshoi" },

        // Sweep - /sweep
        { "sweep", "/sweep" },
        { "broom", "/sweep" },

        // Sundering - /sundering (Endwalker)
        { "sundering", "/sundering" },
        { "exodus", "/sundering" },

        // Ultima - /ultima
        { "ultima", "/ultima" },

        // Megaflare - /megaflare
        { "megaflare", "/megaflare" },

        // Zantetsuken - /zantetsuken
        { "zantetsuken", "/zantetsuken" },
        { "ztk", "/zantetsuken" },

        // Crimson Lotus - /crimsonlotus
        { "crimsonlotus", "/crimsonlotus" },
        { "crimson lotus", "/crimsonlotus" },

        // Stomp - /stomp
        { "stomp", "/stomp" },

        // Divine Arm/Disk/Tiara - Pandaemonium emotes
        { "divinearm", "/divinearm" },
        { "divinedisk", "/divinedisk" },
        { "divinetiara", "/divinetiara" },

        // Ranger Poses (Super Sentai) - only Red, Black, Yellow exist
        // Primary commands are /rangerpose#r and /rangerpose#l
        // Also have full name aliases like /redrangerposea and short aliases like /rrpa

        // Red Ranger Pose A & B
        { "rangerpose1r", "/rangerpose1r" },
        { "redrangerposea", "/rangerpose1r" },
        { "red ranger pose a", "/rangerpose1r" },
        { "rrpa", "/rangerpose1r" },
        { "rangerpose1l", "/rangerpose1l" },
        { "redrangerposeb", "/rangerpose1l" },
        { "red ranger pose b", "/rangerpose1l" },
        { "rrpb", "/rangerpose1l" },

        // Black Ranger Pose A & B
        { "rangerpose2r", "/rangerpose2r" },
        { "blackrangerposea", "/rangerpose2r" },
        { "black ranger pose a", "/rangerpose2r" },
        { "brpa", "/rangerpose2r" },
        { "rangerpose2l", "/rangerpose2l" },
        { "blackrangerposeb", "/rangerpose2l" },
        { "black ranger pose b", "/rangerpose2l" },
        { "brpb", "/rangerpose2l" },

        // Yellow Ranger Pose A & B
        { "rangerpose3r", "/rangerpose3r" },
        { "yellowrangerposea", "/rangerpose3r" },
        { "yellow ranger pose a", "/rangerpose3r" },
        { "yrpa", "/rangerpose3r" },
        { "rangerpose3l", "/rangerpose3l" },
        { "yellowrangerposeb", "/rangerpose3l" },
        { "yellow ranger pose b", "/rangerpose3l" },
        { "yrpb", "/rangerpose3l" },

        // Charmed - /charmed (Valentine)
        { "charmed", "/charmed" },

        // Dazed - /dazed
        { "dazed", "/dazed" },

        // Diamond Dust - /iceheart
        { "iceheart", "/iceheart" },
        { "diamond dust", "/iceheart" },
        { "diamonddust", "/iceheart" },

        // Tremble - /tremble
        { "tremble", "/tremble" },

        // Winded - /winded
        { "winded", "/winded" },

        // Slump - /slump
        { "slump", "/slump" },

        // Rage - /rage
        { "rage", "/rage" },

        // Study - /study
        { "study", "/study" },

        // Carry Book - /carrybook
        { "carrybook", "/carrybook" },

        // Reference - /reference
        { "reference", "/reference" },

        // Linkpearl - /linkpearl
        { "linkpearl", "/linkpearl" },

        // Wow - /wow
        { "wow", "/wow" },

        // Insist - /insist
        { "insist", "/insist" },

        // Shiver - /shiver
        { "shiver", "/shiver" },

        // Shush - /shush
        { "shush", "/shush" },
        { "shh", "/shush" },

        // Overreact - /overreact
        { "overreact", "/overreact" },

        // Pantomime - /pantomime
        { "pantomime", "/pantomime" },
        { "mime", "/pantomime" },

        // Simper - /simper
        { "simper", "/simper" },

        // Scoff - /scoff
        { "scoff", "/scoff" },

        // Sneer - /sneer
        { "sneer", "/sneer" },

        // Smirk - /smirk
        { "smirk", "/smirk" },

        // Smile - /smile
        { "smile", "/smile" },

        // Grin - /grin
        { "grin", "/grin" },

        // Big Grin - /biggrin
        { "biggrin", "/biggrin" },

        // Fake Smile - /fakesmile
        { "fakesmile", "/fakesmile" },

        // Sad - /sad
        { "sad", "/sad" },

        // Scared - /scared
        { "scared", "/scared" },

        // Worried - /worried
        { "worried", "/worried" },

        // Content - /content
        { "content", "/content" },

        // Disturbed - /disturbed
        { "disturbed", "/disturbed" },

        // Shut Eyes - /shuteyes
        { "shuteyes", "/shuteyes" },

        // Concentrate - /concentrate
        { "concentrate", "/concentrate" },

        // Ouch - /ouch
        { "ouch", "/ouch" },

        // Pucker Up - /puckerup
        { "puckerup", "/puckerup" },

        // Consider - /consider
        { "consider", "/consider" },

        // Disappointed - /disappointed
        { "disappointed", "/disappointed" },

        // Annoyed - /annoyed
        { "annoyed", "/annoyed" },

        // Deride - /deride
        { "deride", "/deride" },

        // Vexed - /vexed
        { "vexed", "/vexed" },

        // Elucidate - /elucidate
        { "elucidate", "/elucidate" },

        // Wring Hands - /wringhands
        { "wringhands", "/wringhands" },

        // Hand Over - /handover
        { "handover", "/handover" },

        // Hand to Heart - /handtoheart
        { "handtoheart", "/handtoheart" },

        // At Ease - /atease
        { "atease", "/atease" },

        // Attend - /attend
        { "attend", "/attend" },

        // Eat/Drink emotes
        { "eatapple", "/eatapple" },
        { "apple", "/eatapple" },
        { "eatchicken", "/eatchicken" },
        { "eatpizza", "/eatpizza" },
        { "pizza", "/eatpizza" },
        { "eatchocolate", "/eatchocolate" },
        { "choco", "/eatchocolate" },
        { "eategg", "/eategg" },
        { "egg", "/eategg" },
        { "eatpumpkincookie", "/eatpumpkincookie" },
        { "cookie", "/eatpumpkincookie" },
        { "eatriceball", "/eatriceball" },
        { "riceball", "/eatriceball" },
        { "breakfast", "/breakfast" },
        { "bread", "/breakfast" },
        { "fryegg", "/fryegg" },
        { "drinkgreentea", "/drinkgreentea" },
        { "tea", "/drinkgreentea" },
        { "savortea", "/savortea" },
        { "shakedrink", "/shakedrink" },

        // City sip/gulp emotes
        { "gridaniansip", "/gridaniansip" },
        { "lominsansip", "/lominsansip" },
        { "uldahnsip", "/uldahnsip" },
        { "gridaniangulp", "/gridaniangulp" },
        { "lominsangulp", "/lominsangulp" },
        { "uldahngulp", "/uldahngulp" },

        // Haurchefant - /haurchefant
        { "haurchefant", "/haurchefant" },

        // Humble Triumph - /humbletriumph
        { "humbletriumph", "/humbletriumph" },
        { "waitforit", "/humbletriumph" },

        // Frighten - /frighten
        { "frighten", "/frighten" },

        // Blow Bubbles - /blowbubbles
        { "blowbubbles", "/blowbubbles" },

        // Draw (weapon) - /draw
        { "draw", "/draw" },

        // Show Left/Right - /showleft /showright
        { "showleft", "/showleft" },
        { "showright", "/showright" },

        // Runway Walk - /runwaywalk
        { "runwaywalk", "/runwaywalk" },

        // Stand Up - /standup
        { "standup", "/standup" },

        // Greet - /greet
        { "greet", "/greet" },

        // Spirit - /spirit
        { "spirit", "/spirit" },

        // Spectacles - /spectacles
        { "spectacles", "/spectacles" },

        // Tomescroll - /tomescroll
        { "tomescroll", "/tomescroll" },

        // Pen - /pen
        { "pen", "/pen" },

        // Breath Control - /breathcontrol
        { "breathcontrol", "/breathcontrol" },

        // Water Flip - /waterflip
        { "waterflip", "/waterflip" },

        // Cheer emotes - only real variants from ffxivcollect.com
        // Note: Include space-separated versions as Penumbra may return "Cheer On: Blue" etc.

        // Cheer Jump (only Red and Green exist)
        { "cheerjumpred", "/cheerjumpred" },
        { "cheer jump red", "/cheerjumpred" },
        { "cheer jump: red", "/cheerjumpred" },
        { "cheerjr", "/cheerjr" },  // Short alias
        { "cheerjumpgreen", "/cheerjumpgreen" },
        { "cheer jump green", "/cheerjumpgreen" },
        { "cheer jump: green", "/cheerjumpgreen" },
        { "cheerjg", "/cheerjg" },  // Short alias
        { "cheergreen", "/cheerjumpgreen" },

        // Cheer On (only Blue and Bright exist)
        { "cheeronblue", "/cheeronblue" },
        { "cheer on blue", "/cheeronblue" },
        { "cheer on: blue", "/cheeronblue" },
        { "cheerob", "/cheerob" },  // Short alias
        { "cheeronbright", "/cheeronbright" },
        { "cheer on bright", "/cheeronbright" },
        { "cheer on: bright", "/cheeronbright" },
        { "cheerobr", "/cheerobr" },  // Short alias
        { "cheerbright", "/cheeronbright" },

        // Cheer Wave (only Yellow and Violet exist)
        { "cheerwaveyellow", "/cheerwaveyellow" },
        { "cheer wave yellow", "/cheerwaveyellow" },
        { "cheer wave: yellow", "/cheerwaveyellow" },
        { "cheerwy", "/cheerwy" },  // Short alias
        { "cheerwaveviolet", "/cheerwaveviolet" },
        { "cheer wave violet", "/cheerwaveviolet" },
        { "cheer wave: violet", "/cheerwaveviolet" },
        { "cheerwv", "/cheerwv" },  // Short alias
        { "cheerviolet", "/cheerwaveviolet" },

        // Cheer Rhythm (only Bright, Violet, and Red exist)
        { "cheerrhythmbright", "/cheerrhythmbright" },
        { "cheer rhythm bright", "/cheerrhythmbright" },
        { "cheer rhythm: bright", "/cheerrhythmbright" },
        { "cheerrb", "/cheerrb" },  // Short alias
        { "cheerrhythmviolet", "/cheerrhythmviolet" },
        { "cheer rhythm violet", "/cheerrhythmviolet" },
        { "cheer rhythm: violet", "/cheerrhythmviolet" },
        { "cheerrv", "/cheerrv" },  // Short alias
        { "cheerrhythmred", "/cheerrhythmred" },
        { "cheer rhythm red", "/cheerrhythmred" },
        { "cheer rhythm: red", "/cheerrhythmred" },
        { "cheerrr", "/cheerrr" },  // Short alias

        // Cheer Light (only Green, Blue, and Yellow exist)
        { "cheerlightgreen", "/cheerlightgreen" },
        { "cheer light green", "/cheerlightgreen" },
        { "cheer light: green", "/cheerlightgreen" },
        { "cheerlg", "/cheerlg" },  // Short alias
        { "cheerlightblue", "/cheerlightblue" },
        { "cheer light blue", "/cheerlightblue" },
        { "cheer light: blue", "/cheerlightblue" },
        { "cheerlb", "/cheerlb" },  // Short alias
        { "cheerlightyellow", "/cheerlightyellow" },
        { "cheer light yellow", "/cheerlightyellow" },
        { "cheer light: yellow", "/cheerlightyellow" },
        { "cheerly", "/cheerly" },  // Short alias

        // Simulation emotes
        { "simulationf", "/simulationf" },
        { "simulationm", "/simulationm" },

        // All Saints Charm - /allsaintscharm
        { "allsaintscharm", "/allsaintscharm" },

        // Advent of Light - /adventoflight
        { "adventoflight", "/adventoflight" },
        { "advent", "/adventoflight" },

        // Pose of the Unbound - /poseoftheunbound
        { "poseoftheunbound", "/poseoftheunbound" },
        { "unbound", "/poseoftheunbound" },
        // /pose emote (different from idle/sit pose files)
        { "pose", "/pose" },

        // Jump for Joy variants
        { "jumpforjoy1", "/jumpforjoy1" },
        { "jumpforjoy2", "/jumpforjoy2" },
        { "jumpforjoy3", "/jumpforjoy3" },
        { "jumpforjoy4", "/jumpforjoy4" },
        { "jumpforjoy5", "/jumpforjoy5" },

        // Ear Wiggle - /earwiggle
        { "earwiggle", "/earwiggle" },

        // Visage - /visage
        { "visage", "/visage" },

        // Victory Reveal - /vreveal
        { "vreveal", "/vreveal" },
        { "victoryreveal", "/vreveal" },

        // Paint emotes
        { "paintblack", "/paintblack" },
        { "paintblue", "/paintblue" },
        { "paintred", "/paintred" },
        { "paintyellow", "/paintyellow" },

        // Imperial Salute - /imperialsalute
        { "imperialsalute", "/imperialsalute" },

        // Pagaga / Oho Kaliy - special emotes
        { "pagaga", "/pagaga" },
        { "ohokaliy", "/ohokaliy" },

        // Make It Hail - /makeithail
        { "makeithail", "/makeithail" },

        // Lounge - /lounge (alias for sit)
        { "lounge", "/lounge" },

        // Additional emotes
        { "ritualprayer", "/ritualprayer" },
        { "ritual", "/ritualprayer" },
        { "victorypose", "/victorypose" },
        { "hildibrand", "/hildibrand" },

        // ============================================
        // INTERNAL FFXIV ANIMATION NAMES
        // These are the loop_emot## and act_emot## patterns used internally
        // ============================================
        // Loop emote patterns (loop_emot## -> specific emotes)
        { "loop_emot01", "/joy" },
        { "loop_emot02", "/angry" },
        { "loop_emot03", "/sad" },
        { "loop_emot04", "/cry" },
        { "loop_emot05", "/think" },
        { "loop_emot06", "/doubt" },
        { "loop_emot07", "/blush" },
        { "loop_emot08", "/panic" },
        { "loop_emot09", "/fume" },
        { "loop_emot10", "/sulk" },
        { "loop_emot11", "/laugh" },
        { "loop_emot12", "/cheer" },
        { "loop_emot13", "/rally" },
        { "loop_emot14", "/dance" },
        { "loop_emot15", "/stretch" },
        { "loop_emot16", "/doze" },
        { "loop_emot17", "/flex" },
        { "loop_emot18", "/pray" },
        { "loop_emot19", "/hum" },
        { "loop_emot20", "/stepdance" },
        { "loop_emot21", "/harvestdance" },
        { "loop_emot22", "/golddance" },
        { "loop_emot23", "/balldance" },
        { "loop_emot24", "/mandervilledance" },
        { "loop_emot25", "/bombdance" },
        { "loop_emot26", "/mogdance" },
        { "loop_emot27", "/songbird" },
        { "loop_emot28", "/thavdance" },
        { "loop_emot29", "/beesknees" },
        { "loop_emot30", "/sidestep" },
        { "loop_emot31", "/easterndance" },
        { "loop_emot32", "/sundance" },
        { "loop_emot33", "/moonlift" },
        { "loop_emot34", "/popotostep" },
        { "loop_emot35", "/flamedance" },
        { "loop_emot36", "/yoldance" },
        { "loop_emot37", "/littleladiesdance" },
        { "loop_emot38", "/heeltoe" },
        { "loop_emot39", "/mandervillemambo" },
        { "loop_emot40", "/lalihop" },
        { "loop_emot41", "/lophop" },

        // Action emote patterns (act_emot## -> specific emotes)
        { "act_emot01", "/bow" },
        { "act_emot02", "/wave" },
        { "act_emot03", "/clap" },
        { "act_emot04", "/salute" },
        { "act_emot05", "/kneel" },
        { "act_emot06", "/point" },
        { "act_emot07", "/slap" },
        { "act_emot08", "/hug" },
        { "act_emot09", "/comfort" },
        { "act_emot10", "/flex" },
        { "act_emot11", "/psych" },
        { "act_emot12", "/vpose" },
        { "act_emot13", "/yes" },
        { "act_emot14", "/no" },
        { "act_emot15", "/beckon" },
        { "act_emot16", "/pet" },
        { "act_emot17", "/blowkiss" },

        // ============================================
        // ADDITIONAL FOLDER NAME VARIANTS
        // FFXIV uses various folder naming conventions
        // ============================================
        // Show emotes (folder names)
        { "show_left", "/showleft" },
        { "show_right", "/showright" },
        { "showl", "/showleft" },
        { "showr", "/showright" },

        // Ranger poses (folder names - these have various internal names)
        { "ranger_pose_1l", "/rangerpose1l" },
        { "ranger_pose_1r", "/rangerpose1r" },
        { "ranger_pose_2l", "/rangerpose2l" },
        { "ranger_pose_2r", "/rangerpose2r" },
        { "ranger_pose_3l", "/rangerpose3l" },
        { "ranger_pose_3r", "/rangerpose3r" },
        { "rp1l", "/rangerpose1l" },
        { "rp1r", "/rangerpose1r" },
        { "rp2l", "/rangerpose2l" },
        { "rp2r", "/rangerpose2r" },
        { "rp3l", "/rangerpose3l" },
        { "rp3r", "/rangerpose3r" },
        { "pose_ranger1l", "/rangerpose1l" },
        { "pose_ranger1r", "/rangerpose1r" },
        { "pose_ranger2l", "/rangerpose2l" },
        { "pose_ranger2r", "/rangerpose2r" },
        { "pose_ranger3l", "/rangerpose3l" },
        { "pose_ranger3r", "/rangerpose3r" },

        // Bee's Knees variants (bees_knees already defined above)
        { "bknees", "/beesknees" },
        { "bee_knees", "/beesknees" },

        // Other folder name variants
        { "harvest_dance", "/harvestdance" },
        { "gold_dance", "/golddance" },
        { "ball_dance", "/balldance" },
        { "step_dance", "/stepdance" },
        { "bomb_dance", "/bombdance" },
        { "mog_dance", "/mogdance" },
        { "moogle_dance", "/mogdance" },
        { "thav_dance", "/thavdance" },
        { "side_step", "/sidestep" },
        { "eastern_dance", "/easterndance" },
        { "e_dance", "/easterndance" },
        { "sun_drop", "/sundance" },
        { "moon_drop", "/moonlift" },
        { "moon_lift", "/moonlift" },
        { "popoto_step", "/popotostep" },
        { "flame_dance", "/flamedance" },
        { "yol_dance", "/yoldance" },
        { "la_dance", "/littleladiesdance" },
        { "heel_toe", "/heeltoe" },
        { "lali_hop", "/lalihop" },
        { "lo_phop", "/lophop" },
    };

    // Common non-emote game commands that should also be protected
    // NOTE: Only include commands 3+ characters to avoid false positives while typing
    // Source: https://na.finalfantasyxiv.com/lodestone/playguide/db/text_command/
    private static readonly HashSet<string> OtherGameCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // ============================================
        // CHAT COMMANDS
        // ============================================
        "say", "yell", "shout", "tell", "reply", "cleartellhistory",
        "party", "alliance", "freecompany", "pvpteam",
        "cwlinkshell", "cwlinkshell1", "cwlinkshell2", "cwlinkshell3", "cwlinkshell4",
        "cwlinkshell5", "cwlinkshell6", "cwlinkshell7", "cwlinkshell8",
        "linkshell", "linkshell1", "linkshell2", "linkshell3", "linkshell4",
        "linkshell5", "linkshell6", "linkshell7", "linkshell8",
        "novice", "echo", "clearlog", "quickchat", "emote", "emotelog",

        // ============================================
        // PARTY & SOCIAL COMMANDS
        // ============================================
        "join", "decline", "invite", "kick", "leader", "leave", "disband",
        "partycmd", "partysort", "recordready", "readycheck", "ready", "hold",
        "countdown", "strategyboard", "friendlist", "blacklist",
        "search", "slist", "cwsearch", "examine", "trade",
        "lookingforparty", "lookingtomeld", "searchcomment",
        "ridepillion", "meldrequest",

        // ============================================
        // TARGET COMMANDS
        // ============================================
        "check", "target", "targetpc", "targetnpc", "targetenemy",
        "battletarget", "assist", "facetarget", "nexttarget", "previoustarget",
        "targetlasttarget", "targetlastenemy", "lockon", "focustarget",
        "enemysign", "targetself", "automove", "follow",

        // ============================================
        // HOTBAR COMMANDS
        // ============================================
        "action", "blueaction", "pvpaction", "generalaction",
        "companionaction", "petaction", "mount", "minion", "fashion", "facewear",
        "recast", "additionalaction", "bluespellbook", "addpvpaction",
        "hotbar", "pvphotbar", "crosshotbar", "pvpcrosshotbar",

        // ============================================
        // BATTLE COMMANDS
        // ============================================
        "battlemode", "statusoff", "waymark",

        // ============================================
        // PET COMMANDS
        // ============================================
        "petglamour", "egiglamour", "petsize", "bahamutsize",

        // ============================================
        // SYSTEM & UI COMMANDS
        // ============================================
        "instance", "title", "gearset", "itemsort", "itemsearch",
        "levelsync", "visor", "legacymark", "facecamera", "grouppose", "gpose",
        "idlingcamera", "alarm", "commandpanel",
        "hud", "hudreset", "uireset", "uiscale", "jobhudmode", "hudlayout",
        "busy", "away", "afk", "online", "roleplaying",
        "dice", "random", "playtime", "logout", "shutdown",
        "nastatus", "returnerstatusoff",
        "novicenetworkinvitation", "novicenetworkleave", "novicenetwork",
        "duelswitch", "patchnote",

        // ============================================
        // MAGIA BOARD (EUREKA/BOZJA)
        // ============================================
        "magiaright", "magialeft", "magiaattack", "magiadefense", "magiaauto",

        // ============================================
        // MACRO COMMANDS
        // ============================================
        "wait", "macroicon", "micon", "macrolock", "mlock",
        "macroerror", "macrocancel", "macro", "macros",

        // ============================================
        // CAMERA & DISPLAY SETTINGS
        // ============================================
        "tiltcamera", "autolockon", "autofacetarget",
        "targetring", "targetline", "aggroline",
        "autotarget", "autotargetpriority",
        "displayhead", "displayarms", "display",
        "vieraears", "autosheathe", "battleeffect",
        "rclickpc", "rclickbattlenpc", "rclickminion", "groundclick",
        "nameplatedisp", "nameplatetype", "nameplate",
        "crosshotbardisplay", "crosshotbartype",
        "chatlog", "actionerror", "recasterror",
        "camera", "fov", "screenshot",

        // ============================================
        // AUDIO SETTINGS
        // ============================================
        "graphicpresets", "mastervolume", "bgm",
        "soundeffects", "voice", "systemsounds", "ambientsounds",
        "soundeffectsself", "soundeffectsparty", "soundeffectsother",
        "systemsoundsspeaker", "performsounds", "mountbgm",
        "visualalerts", "colorfiltering",

        // ============================================
        // MAIN MENU / WINDOWS
        // ============================================
        "character", "armourychest", "inventory", "saddlebag",
        "companion", "mountguide", "minionguide", "facewearlist", "fashionguide",
        "pvpprofile", "goldsaucer", "gold", "achievements", "recommended",
        "collection", "keyitem",
        "journal", "quest", "duty", "dutyfinder", "raidfinder", "record",
        "timers", "timer", "generaldutykey",
        "huntinglog", "sightseeinglog", "craftinglog", "gatheringlog",
        "fishinglog", "fishguide", "orchestrion", "challengelog",
        "aethercurrent", "mountspeed", "aethernet",
        "map", "teleport", "return",
        "partyfinder", "fellowshipfinder", "emotelist", "actionlist",
        "freecompanycmd", "housing", "pvpteamcmd", "pvp",
        "linkshellcmd", "cwlinkshellcmd", "fellowship",
        "contactlist", "mutelist", "termfilter",
        "supportdesk", "officialsite", "playguide",

        // ============================================
        // CONFIGURATION
        // ============================================
        "activehelp", "characterconfig", "systemconfig",
        "keybind", "logcolor", "marking"
    };

    public static HashSet<string> GetKnownGameCommands()
    {
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add all emote commands (extract command name without slash)
        foreach (var cmd in EmoteToCommand.Values.Distinct())
        {
            var cmdName = cmd.TrimStart('/');
            commands.Add(cmdName);
        }

        // Add other game commands
        foreach (var cmd in OtherGameCommands)
        {
            commands.Add(cmd);
        }

        return commands;
    }

    public static bool IsKnownGameCommand(string command)
    {
        var cmd = command.TrimStart('/').ToLowerInvariant();

        // Skip very short commands to avoid false positives while typing
        if (cmd.Length < 3)
            return false;

        // Check emotes
        if (EmoteToCommand.Values.Any(v => v.Equals($"/{cmd}", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check other game commands
        return OtherGameCommands.Contains(cmd);
    }

    // Animation types (same as CS+)
    public enum AnimationType
    {
        None,
        Emote,           // Dance, hum, expressions - execute command
        StandingIdle,    // Idle poses - needs redraw
        ChairSitting,    // /sit poses - needs redraw
        GroundSitting,   // /groundsit poses - needs redraw
        LyingDozing,     // /doze poses - needs redraw
        Movement,
    }

    public EmoteDetectionService(PenumbraService penumbraService, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.penumbraService = penumbraService;
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.cache = new EmoteModCache(pluginInterface, log);
    }

    public void InitializeCacheAsync(bool force = false)
    {
        if (!force && (isInitializing || cache.IsInitialized))
            return;

        isInitializing = true;
        var currentGeneration = ++scanGeneration;  // Increment and capture the generation

        Task.Run(() =>
        {
            try
            {
                InitializeCache(currentGeneration);
            }
            catch (Exception ex)
            {
                log.Error($"[EmoteDetection] Failed to initialize cache: {ex.Message}");
            }
            finally
            {
                // Only clear isInitializing if this is still the current scan
                if (scanGeneration == currentGeneration)
                {
                    isInitializing = false;
                }
            }
        });
    }

    private void InitializeCache(int generation)
    {
        log.Information("[EmoteDetection] Initializing emote mod cache...");

        // Step 1: Load disk cache (but don't mark as initialized yet)
        var diskCache = cache.LoadFromDisk();
        cache.InitializeFromDisk(diskCache, markInitialized: false);

        // Check if cancelled
        if (scanGeneration != generation)
        {
            log.Information("[EmoteDetection] Scan cancelled (new scan started)");
            return;
        }

        // Step 2: Get current mod list from Penumbra
        var modList = penumbraService.GetModList();
        if (modList == null || modList.Count == 0)
        {
            log.Information("[EmoteDetection] No mods found in Penumbra");
            if (scanGeneration == generation)
                cache.SetInitialized();
            return;
        }

        log.Information($"[EmoteDetection] Scanning {modList.Count} mods...");

        int newModCount = 0;
        int cachedCount = 0;
        int emoteModCount = 0;
        bool needsSave = false;
        int processedCount = 0;

        foreach (var (modDir, modName) in modList)
        {
            // Check for cancellation periodically
            if (scanGeneration != generation)
            {
                log.Information($"[EmoteDetection] Scan cancelled at {processedCount}/{modList.Count} mods");
                return;
            }

            try
            {
                processedCount++;

                // Check if already cached
                if (cache.HasCachedEntry(modDir))
                {
                    cachedCount++;
                    var cached = cache.GetCachedEntry(modDir);
                    if (cached?.IsEmoteMod == true)
                        emoteModCount++;
                    continue;
                }

                // New mod - analyze it
                var emoteInfo = AnalyzeModInternal(modDir, modName);
                cache.UpdateEntry(modDir, modName, emoteInfo);

                if (emoteInfo != null)
                    emoteModCount++;

                newModCount++;
                needsSave = true;

                // Log progress every 500 mods
                if (processedCount % 500 == 0)
                {
                    log.Debug($"[EmoteDetection] Progress: {processedCount}/{modList.Count} mods processed...");
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[EmoteDetection] Error analyzing mod {modName}: {ex.Message}");
            }
        }

        // Check if cancelled before finalizing
        if (scanGeneration != generation)
        {
            log.Information("[EmoteDetection] Scan cancelled before finalization");
            return;
        }

        // Step 3: Save cache if changed
        if (needsSave || diskCache == null)
        {
            cache.SaveToDisk();
        }

        // Step 4: Mark as fully initialized AFTER scanning completes
        cache.SetInitialized();

        log.Information($"[EmoteDetection] Cache initialized: {cachedCount} cached, {newModCount} new, {emoteModCount} emote mods total");
    }

    /// <summary>
    /// Get all emote mods (uses cache)
    /// Returns whatever is cached so far, even during scanning
    /// </summary>
    public List<EmoteModInfo> GetEmoteMods()
    {
        // Return whatever we have cached, even if scan isn't complete
        // This lets the UI show progress as mods are discovered
        return cache.GetCachedEmoteMods()
            .OrderBy(m => m.ModName)
            .ToList();
    }

    /// <summary>
    /// Analyze a specific mod (uses cache if available, does NOT update cache)
    /// This is used by the UI when clicking on mods - we don't want to overwrite
    /// cache data that might be better from the full background scan.
    /// </summary>
    public EmoteModInfo? AnalyzeMod(string modDirectory, string modName)
    {
        // Check cache first - this is the preferred source
        var cached = cache.GetCachedEntry(modDirectory);
        if (cached != null)
        {
            // Return cached data even if IsEmoteMod is false - UI will show "unknown"
            // but we won't corrupt the cache by re-analyzing
            // Clone lists to prevent callers from mutating cache data
            return new EmoteModInfo
            {
                ModDirectory = modDirectory,
                ModName = cached.Name,
                AffectedEmotes = new List<string>(cached.AffectedEmotes ?? new List<string>()),
                EmoteCommands = new List<string>(cached.EmoteCommands ?? new List<string>()),
                PrimaryEmote = cached.PrimaryEmote ?? "",
                EmoteCommand = cached.PrimaryCommand ?? "",
                AnimationType = (AnimationType)cached.AnimationType,
                PoseIndex = cached.PoseIndex,
                AffectedPoseIndices = new List<int>(cached.AffectedPoseIndices ?? new List<int>())
            };
        }

        // Not cached - analyze but DON'T update cache
        // The background scan will handle caching properly
        // This prevents incomplete analysis from overwriting good cache data
        var result = AnalyzeModInternal(modDirectory, modName);
        return result;
    }

    /// <summary>
    /// Internal method to analyze a mod (no caching)
    /// MERGES results from both file path analysis AND Penumbra's GetChangedItems
    /// to get the most complete picture of what emotes a mod affects.
    /// </summary>
    private EmoteModInfo? AnalyzeModInternal(string modDirectory, string modName)
    {
        var affectedEmotes = new List<string>();
        var emoteCommands = new List<string>();
        var emotePaths = new List<string>();

        // Track animation type and pose indices from file path analysis
        AnimationType detectedAnimationType = AnimationType.None;
        var detectedPoseIndices = new HashSet<int>();

        // 1. Get emotes from file path analysis (good for specific IDs like loop_emot19)
        var filePathResult = AnalyzeModByFilePaths(modDirectory, modName);
        if (filePathResult != null)
        {
            foreach (var emote in filePathResult.AffectedEmotes)
            {
                if (!affectedEmotes.Contains(emote))
                    affectedEmotes.Add(emote);
            }
            foreach (var cmd in filePathResult.EmoteCommands)
            {
                if (!emoteCommands.Contains(cmd))
                    emoteCommands.Add(cmd);
            }
            emotePaths = filePathResult.EmotePaths;

            // Preserve animation type and pose indices from file path analysis
            detectedAnimationType = filePathResult.AnimationType;
            if (filePathResult.AffectedPoseIndices.Count > 0)
                foreach (var idx in filePathResult.AffectedPoseIndices)
                    detectedPoseIndices.Add(idx);
            else if (filePathResult.PoseIndex >= 0)
                detectedPoseIndices.Add(filePathResult.PoseIndex);
        }

        // 2. ALSO get emotes from Penumbra's GetChangedItems
        // GetChangedItems returns game paths OR item names that the mod affects
        var changedItems = penumbraService.GetModChangedItems(modDirectory, modName);

        // First pass: detect pose mods from game paths in changedItems
        foreach (var (itemName, _) in changedItems)
        {
            var itemLower = itemName.ToLowerInvariant();

            // Check if this is a game path (contains slashes and .pap extension)
            if (itemLower.Contains("/") && itemLower.EndsWith(".pap"))
            {
                // Standing idle detection
                if (itemLower.Contains("/resident/") &&
                    (itemLower.Contains("idle") || System.Text.RegularExpressions.Regex.IsMatch(itemLower, @"pose\d*\.pap")))
                {
                    detectedAnimationType = AnimationType.StandingIdle;
                    var poseIdx = ExtractPoseIndexFromPath(itemLower, "pose");
                    if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                    else if (itemLower.Contains("idle.pap")) detectedPoseIndices.Add(0);
                }
                // Chair sitting
                else if (itemLower.Contains("/sit/") || itemLower.Contains("s_pose"))
                {
                    detectedAnimationType = AnimationType.ChairSitting;
                    var poseIdx = ExtractPoseIndexFromPath(itemLower, "s_pose");
                    if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                }
                // Ground sitting
                else if (itemLower.Contains("/jmn/") || itemLower.Contains("j_pose"))
                {
                    detectedAnimationType = AnimationType.GroundSitting;
                    var poseIdx = ExtractPoseIndexFromPath(itemLower, "j_pose");
                    if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                }
                // Doze
                else if (itemLower.Contains("/doze/") || itemLower.Contains("l_pose"))
                {
                    detectedAnimationType = AnimationType.LyingDozing;
                    var poseIdx = ExtractPoseIndexFromPath(itemLower, "l_pose");
                    if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                }
            }
        }

        bool isPoseMod = detectedAnimationType == AnimationType.StandingIdle ||
                         detectedAnimationType == AnimationType.ChairSitting ||
                         detectedAnimationType == AnimationType.GroundSitting ||
                         detectedAnimationType == AnimationType.LyingDozing;

        // Second pass: extract emote names
        foreach (var (itemName, _) in changedItems)
        {
            var itemLower = itemName.ToLowerInvariant();

            // Skip game paths (already processed above)
            if (itemLower.Contains("/") && itemLower.Contains("."))
                continue;

            // Skip non-emote items
            if (itemLower.Contains("equipment") ||
                itemLower.Contains("weapon") ||
                itemLower.Contains("armor") ||
                itemLower.Contains("accessory") ||
                itemLower.Contains("mount") ||
                itemLower.Contains("teleport") ||
                itemLower.Contains("return") ||
                itemLower.Contains("sprint") ||
                itemLower.Contains("limit break") ||
                itemLower.Contains("duty action"))
                continue;

            // Clean up the name - Penumbra returns "Emote: Harvest Dance" or similar
            var cleanName = itemLower
                .Replace("emote: ", "")
                .Replace("emote:", "")
                .Replace("action: ", "")
                .Replace("action:", "")
                .Trim();

            // If this is a pose mod (idle/sit/doze), skip adding the /pose command
            // because "pose" in GetChangedItems refers to idle poses, not the /pose emote
            if (isPoseMod && cleanName == "pose")
                continue;

            // Try direct match first
            if (EmoteToCommand.TryGetValue(cleanName, out var command))
            {
                if (!affectedEmotes.Contains(cleanName))
                    affectedEmotes.Add(cleanName);
                if (!emoteCommands.Contains(command))
                    emoteCommands.Add(command);
            }
            // Try partial match for emote-related items
            else if (itemLower.Contains("dance") ||
                     itemLower.Contains("emote") ||
                     itemLower.Contains("gesture") ||
                     itemLower.Contains("expression") ||
                     itemLower.Contains("hum"))
            {
                // Try to match against known emotes (longest first)
                foreach (var (emoteName, emoteCmd) in EmoteToCommand.OrderByDescending(e => e.Key.Length))
                {
                    // Check if the clean name contains or matches this emote
                    if (cleanName.Contains(emoteName.ToLowerInvariant()) ||
                        emoteName.ToLowerInvariant().Contains(cleanName))
                    {
                        if (!affectedEmotes.Contains(emoteName))
                            affectedEmotes.Add(emoteName);
                        if (!emoteCommands.Contains(emoteCmd))
                            emoteCommands.Add(emoteCmd);
                        break;
                    }
                }
            }
        }

        // If we found nothing, return null
        if (affectedEmotes.Count == 0 && emoteCommands.Count == 0)
        {
            return filePathResult; // Return file path result even if empty (for emotePaths)
        }

        // Sort emotes: specific dances first, then others, generic "dance" last
        var sortedEmotes = affectedEmotes
            .OrderByDescending(e => e.Length)
            .ThenByDescending(e => e.StartsWith("dance") && e.Length > 5 && char.IsDigit(e[5]) ? 1 : 0)
            .ThenBy(e => e == "dance" ? 1 : 0)
            .ToList();

        var sortedCommands = sortedEmotes
            .Where(e => EmoteToCommand.ContainsKey(e))
            .Select(e => EmoteToCommand[e])
            .Distinct()
            .ToList();

        // Add any commands we found that aren't in the sorted list
        foreach (var cmd in emoteCommands)
        {
            if (!sortedCommands.Contains(cmd))
                sortedCommands.Add(cmd);
        }

        var primaryEmote = sortedEmotes.FirstOrDefault() ?? "";
        var primaryCommand = sortedCommands.FirstOrDefault() ?? "";

        var sortedPoseIndices = detectedPoseIndices.OrderBy(i => i).ToList();
        int detectedPoseIndex = sortedPoseIndices.Count > 0 ? sortedPoseIndices[0] : -1;

        return new EmoteModInfo
        {
            ModDirectory = modDirectory,
            ModName = modName,
            AffectedEmotes = sortedEmotes,
            EmoteCommands = sortedCommands,
            EmotePaths = emotePaths,
            PrimaryEmote = primaryEmote,
            EmoteCommand = primaryCommand,
            AnimationType = detectedAnimationType,
            PoseIndex = detectedPoseIndex,
            AffectedPoseIndices = sortedPoseIndices
        };
    }

    /// <summary>
    /// Fallback: Analyze mod by reading file paths from JSON
    /// </summary>
    private EmoteModInfo? AnalyzeModByFilePaths(string modDirectory, string modName)
    {
        var modFiles = GetModFilePaths(modDirectory);
        if (modFiles.Count == 0)
            return null;

        var emotePaths = new List<string>();
        var posePaths = new List<string>();
        int emoteCount = 0;
        int poseCount = 0;
        int otherCount = 0;
        int weaponCount = 0;

        // Track detected animation type and pose indices from paths
        AnimationType detectedType = AnimationType.None;
        var detectedPoseIndices = new HashSet<int>();

        foreach (var path in modFiles)
        {
            var pathLower = path.ToLowerInvariant();

            // Track weapon files - if mostly weapons, skip this mod
            if (pathLower.Contains("/weapon/") || pathLower.Contains("_weapon") ||
                pathLower.Contains("/action/") || pathLower.Contains("ability/"))
            {
                weaponCount++;
                continue;
            }

            // Filter out weapon stance animations (bt_stf, bt_2sw, bt_nin_nin, etc.)
            // These are job-specific battle stances, NOT the bt_common folder
            // Pattern: bt_ followed by anything other than "common"
            if (System.Text.RegularExpressions.Regex.IsMatch(pathLower, @"/bt_(?!common)[a-z0-9_]+/"))
            {
                weaponCount++;
                continue;
            }

            // Check for pose files FIRST (they're in specific locations)
            // Standing idle patterns:
            //   /resident/idle.pap, /resident/pose##.pap (standard)
            //   bt_common/resident/idle.pap (some mods)
            //   Files with idle_pose or standing pose patterns
            if (pathLower.EndsWith(".pap"))
            {
                // Standard resident folder pattern
                if (pathLower.Contains("/resident/") || pathLower.Contains("bt_common/resident"))
                {
                    if (pathLower.Contains("idle.pap") ||
                        System.Text.RegularExpressions.Regex.IsMatch(pathLower, @"pose\d+\.pap$"))
                    {
                        poseCount++;
                        posePaths.Add(path);
                        detectedType = AnimationType.StandingIdle;
                        // Extract pose index from filename (pose01.pap -> 1, idle.pap -> 0)
                        var poseIdx = ExtractPoseIndexFromPath(pathLower, "pose");
                        if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                        else if (pathLower.Contains("idle.pap")) detectedPoseIndices.Add(0);
                        continue;
                    }
                }

                // Also check for standalone idle animation files (not in emote folder)
                // These are files like idle_sp.pap, idle_loop.pap outside of /emote/
                if (!pathLower.Contains("/emote/") && !pathLower.Contains("emote/"))
                {
                    var filename = Path.GetFileName(pathLower);
                    if (filename.StartsWith("idle") && !filename.Contains("emot"))
                    {
                        poseCount++;
                        posePaths.Add(path);
                        if (detectedType == AnimationType.None)
                            detectedType = AnimationType.StandingIdle;
                        var poseIdx = ExtractPoseIndexFromPath(pathLower, "pose");
                        if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                        continue;
                    }
                }
            }

            // Chair sitting: /sit/ folder or s_pose files
            if ((pathLower.Contains("/sit/") || pathLower.Contains("s_pose")) && pathLower.EndsWith(".pap"))
            {
                poseCount++;
                posePaths.Add(path);
                detectedType = AnimationType.ChairSitting;
                var poseIdx = ExtractPoseIndexFromPath(pathLower, "s_pose");
                if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                continue;
            }

            // Ground sitting: /jmn/ folder or j_pose files
            if ((pathLower.Contains("/jmn/") || pathLower.Contains("j_pose")) && pathLower.EndsWith(".pap"))
            {
                poseCount++;
                posePaths.Add(path);
                detectedType = AnimationType.GroundSitting;
                var poseIdx = ExtractPoseIndexFromPath(pathLower, "j_pose");
                if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                continue;
            }

            // Doze: /doze/ folder or l_pose files
            if ((pathLower.Contains("/doze/") || pathLower.Contains("l_pose")) && pathLower.EndsWith(".pap"))
            {
                poseCount++;
                posePaths.Add(path);
                detectedType = AnimationType.LyingDozing;
                var poseIdx = ExtractPoseIndexFromPath(pathLower, "l_pose");
                if (poseIdx >= 0) detectedPoseIndices.Add(poseIdx);
                continue;
            }

            // Standing idle poses in /emote/ folder: pose##_loop.pap pattern
            // These are the idle poses you cycle through with /cpose, NOT the /pose emote
            if (pathLower.Contains("/emote/") && pathLower.EndsWith(".pap"))
            {
                var filename = Path.GetFileName(pathLower);
                // Match pose##_loop.pap pattern (pose01_loop.pap, pose02_loop.pap, etc.)
                var idlePoseMatch = System.Text.RegularExpressions.Regex.Match(filename, @"^pose(\d+)_loop\.pap$");
                if (idlePoseMatch.Success)
                {
                    poseCount++;
                    posePaths.Add(path);
                    detectedType = AnimationType.StandingIdle;
                    if (int.TryParse(idlePoseMatch.Groups[1].Value, out var poseIdx))
                        detectedPoseIndices.Add(poseIdx);
                    continue;
                }
            }

            // Look for emote files - can be /emote/ folder, bt_common/emote/ path,
            // or emote_sp/ folder (special emotes like Show Left/Right)
            // Path examples:
            //   /emote/beesknees/file.pap
            //   bt_common/emote/dance04_loop.pap
            //   bt_common/emote_sp/sp41_loop.pap
            if (pathLower.Contains("emote/") || pathLower.Contains("emote_sp/") || pathLower.Contains("/emote"))
            {
                // Skip movement files
                if (pathLower.Contains("move_"))
                {
                    otherCount++;
                    continue;
                }

                emoteCount++;
                emotePaths.Add(path);
                if (detectedType == AnimationType.None)
                    detectedType = AnimationType.Emote;
                continue;
            }

            // Count other animation files (but skip lip sync / facial animations -
            // these commonly accompany dance mods and shouldn't count against them)
            if (pathLower.Contains(".pap") && !pathLower.Contains("/nonresident/"))
                otherCount++;
        }

        // Must have emote or pose files
        if (emoteCount == 0 && poseCount == 0)
            return null;

        // If mostly weapon files, skip (weapon stance mod, not emote)
        var relevantCount = emoteCount + poseCount;
        if (weaponCount > relevantCount)
            return null;

        // If more non-emote than emote/pose files, skip (likely not an emote/pose mod)
        if (otherCount > relevantCount * 2)
            return null;

        // Extract emote names from the paths
        var affectedEmotes = new List<string>();
        var emoteCommands = new List<string>();

        // Process emote paths
        foreach (var path in emotePaths)
        {
            var emoteName = ExtractEmoteNameFromPath(path);
            if (!string.IsNullOrEmpty(emoteName) && EmoteToCommand.TryGetValue(emoteName, out var cmd))
            {
                if (!affectedEmotes.Contains(emoteName))
                    affectedEmotes.Add(emoteName);
                if (!emoteCommands.Contains(cmd))
                    emoteCommands.Add(cmd);
            }
        }

        // For pose mods, add appropriate commands based on type
        if (poseCount > 0)
        {
            switch (detectedType)
            {
                case AnimationType.StandingIdle:
                    if (!affectedEmotes.Contains("idle"))
                        affectedEmotes.Add("idle");
                    if (!emoteCommands.Contains("/cpose"))
                        emoteCommands.Add("/cpose");
                    break;
                case AnimationType.ChairSitting:
                    if (!affectedEmotes.Contains("sit"))
                        affectedEmotes.Add("sit");
                    if (!emoteCommands.Contains("/sit"))
                        emoteCommands.Add("/sit");
                    break;
                case AnimationType.GroundSitting:
                    if (!affectedEmotes.Contains("groundsit"))
                        affectedEmotes.Add("groundsit");
                    if (!emoteCommands.Contains("/groundsit"))
                        emoteCommands.Add("/groundsit");
                    break;
                case AnimationType.LyingDozing:
                    if (!affectedEmotes.Contains("doze"))
                        affectedEmotes.Add("doze");
                    if (!emoteCommands.Contains("/doze"))
                        emoteCommands.Add("/doze");
                    break;
            }
        }

        // Prioritize specific emotes over generic ones
        // Numbered dances (dance02, dance03, etc.) should come before generic "dance"
        // This prevents /harvestdance from being overridden by /dance
        var sortedEmotes = affectedEmotes
            .OrderByDescending(e => e.Length)  // Longer names first (more specific)
            .ThenByDescending(e => e.StartsWith("dance") && e.Length > 5 ? 1 : 0)  // Numbered dances first
            .ThenBy(e => e == "dance" ? 1 : 0)  // Generic "dance" last
            .ToList();

        var primaryEmote = sortedEmotes.FirstOrDefault() ?? "";
        var primaryCommand = !string.IsNullOrEmpty(primaryEmote) && EmoteToCommand.TryGetValue(primaryEmote, out var primaryCmd)
            ? primaryCmd
            : emoteCommands.FirstOrDefault() ?? "";

        // Build sorted command list to match sorted emotes
        var sortedCommands = sortedEmotes
            .Where(e => EmoteToCommand.ContainsKey(e))
            .Select(e => EmoteToCommand[e])
            .Distinct()
            .ToList();

        // Add any commands we found that aren't in sorted list
        foreach (var cmd in emoteCommands)
        {
            if (!sortedCommands.Contains(cmd))
                sortedCommands.Add(cmd);
        }

        // Combine all paths
        var allPaths = emotePaths.Concat(posePaths).ToList();

        // Determine pose indices
        var sortedPoseIndices = detectedPoseIndices.OrderBy(i => i).ToList();
        int poseIndex = sortedPoseIndices.Count > 0 ? sortedPoseIndices[0] : -1;

        // Return mod info
        return new EmoteModInfo
        {
            ModDirectory = modDirectory,
            ModName = modName,
            AffectedEmotes = sortedEmotes,
            EmoteCommands = sortedCommands,
            EmotePaths = allPaths,
            PrimaryEmote = primaryEmote,
            EmoteCommand = primaryCommand,
            AnimationType = detectedType,
            PoseIndex = poseIndex,
            AffectedPoseIndices = sortedPoseIndices
        };
    }

    private int ExtractPoseIndexFromPath(string pathLower, string prefix)
    {
        try
        {
            // Find the prefix in the path
            var idx = pathLower.LastIndexOf(prefix);
            if (idx < 0)
                return -1;

            // Get the part after the prefix
            var afterPrefix = pathLower.Substring(idx + prefix.Length);

            // Extract digits
            var digits = "";
            foreach (var c in afterPrefix)
            {
                if (char.IsDigit(c))
                    digits += c;
                else
                    break;
            }

            if (string.IsNullOrEmpty(digits))
                return -1;

            return int.Parse(digits);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Get file paths from a mod by reading its JSON files directly.
    /// Same approach as CS+ GetModFilePathsFromJson.
    /// </summary>
    private List<string> GetModFilePaths(string modDirectory)
    {
        var filePaths = new List<string>();

        try
        {
            var penumbraModPath = penumbraService.GetModDirectory();
            if (string.IsNullOrEmpty(penumbraModPath))
                return filePaths;

            var fullModPath = Path.Combine(penumbraModPath, modDirectory);
            if (!Directory.Exists(fullModPath))
                return filePaths;

            foreach (var file in Directory.EnumerateFiles(fullModPath, "*.json"))
            {
                if (file.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var jsonContent = File.ReadAllText(file);

                    if (file.EndsWith("default_mod.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var option = JsonSerializer.Deserialize<ModOption>(jsonContent);
                        if (option?.Files != null)
                        {
                            foreach (var kvp in option.Files)
                            {
                                if (!string.IsNullOrEmpty(kvp.Key))
                                    filePaths.Add(kvp.Key);
                            }
                        }
                    }
                    else
                    {
                        var group = JsonSerializer.Deserialize<ModGroup>(jsonContent);
                        if (group?.Options != null)
                        {
                            foreach (var option in group.Options)
                            {
                                if (option?.Files != null)
                                {
                                    foreach (var kvp in option.Files)
                                    {
                                        if (!string.IsNullOrEmpty(kvp.Key))
                                            filePaths.Add(kvp.Key);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Skip files that fail to parse
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[EmoteDetection] Error reading mod JSON files for '{modDirectory}': {ex.Message}");
        }

        return filePaths;
    }

    /// <summary>
    /// Extract emote name from a file path and try to find a matching command.
    /// Returns the emote identifier that should be looked up in EmoteToCommand.
    /// Handles paths like:
    ///   - /emote/beesknees/file.pap (folder name is emote)
    ///   - bt_common/emote/dance04_loop.pap (filename is emote)
    ///   - emote/dance03/dance_female_loop.pap (folder dance03 = harvestdance, not dance_female)
    /// </summary>
    private string ExtractEmoteNameFromPath(string path)
    {
        var pathLower = path.ToLowerInvariant();

        // Check for emote_sp/ paths FIRST (special emotes like Show Left/Right use sp## naming)
        // Example: bt_common/emote_sp/sp41_loop.pap -> sp41 -> showleft
        var emoteSpIdx = pathLower.IndexOf("emote_sp/");
        if (emoteSpIdx >= 0)
        {
            var afterEmoteSp = pathLower.Substring(emoteSpIdx + 9); // length of "emote_sp/"
            // Extract the sp## identifier from the filename (e.g., "sp41_loop.pap" -> "sp41")
            var spFile = afterEmoteSp;
            var slashIdx = spFile.IndexOf('/');
            if (slashIdx >= 0)
                spFile = spFile.Substring(slashIdx + 1);
            // Remove .pap extension
            if (spFile.EndsWith(".pap"))
                spFile = spFile.Substring(0, spFile.Length - 4);
            // Remove animation suffixes (_loop, _start, _end)
            spFile = RemoveAnimationSuffixes(spFile);
            // Look up in SpToEmote mapping
            if (SpToEmote.TryGetValue(spFile, out var emoteName))
                return emoteName;
        }

        // For dance paths, check FOLDER first since numbered dances (dance02, dance03, etc.)
        // use folder names to identify the specific emote, while filenames are just race/gender variants.
        // Example: emote/dance03/dance_female_loop.pap -> dance03 (harvestdance), NOT dance_female (generic dance)
        var emoteIdx = pathLower.IndexOf("emote/");
        if (emoteIdx >= 0)
        {
            var afterEmote = pathLower.Substring(emoteIdx + 6);
            var slashIdx = afterEmote.IndexOf('/');
            if (slashIdx > 0)
            {
                var folderName = afterEmote.Substring(0, slashIdx);

                // Check if folder is a numbered dance (dance02, dance03, etc.) - these are specific emotes
                if (folderName.StartsWith("dance") && folderName.Length >= 7 && char.IsDigit(folderName[5]))
                {
                    if (EmoteToCommand.ContainsKey(folderName))
                        return folderName;
                }

                // Also try folder name directly for other emotes (beesknees, hum, etc.)
                var candidates = GenerateEmoteCandidates(folderName);
                foreach (var candidate in candidates)
                {
                    if (EmoteToCommand.ContainsKey(candidate))
                        return candidate;
                }
            }
        }

        // Then try extracting from the filename
        // Example: bt_common/emote/dance04_loop.pap -> dance04
        var lastSlash = pathLower.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var filename = pathLower.Substring(lastSlash + 1);
            if (filename.EndsWith(".pap"))
            {
                filename = filename.Substring(0, filename.Length - 4);

                // Try to match the filename
                var candidates = GenerateEmoteCandidates(filename);
                foreach (var candidate in candidates)
                {
                    if (EmoteToCommand.ContainsKey(candidate))
                        return candidate;
                }

                // Try partial match on filename
                var match = FindLongestEmoteMatch(filename);
                if (!string.IsNullOrEmpty(match))
                    return match;
            }
        }

        // Folder check was already done at the start of this function
        return "";
    }

    /// <summary>
    /// Find the longest matching emote name that appears in the given text.
    /// Returns the emote key (not the command).
    /// </summary>
    private string FindLongestEmoteMatch(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        // Sort by key length descending to match longest first
        // This ensures "harvestdance" matches before "dance", "showleft" before "show", etc.
        var sortedEmotes = EmoteToCommand
            .OrderByDescending(e => e.Key.Length)
            .ToList();

        foreach (var (emoteName, _) in sortedEmotes)
        {
            // Skip very short names that could match too broadly
            if (emoteName.Length < 3)
                continue;

            // Skip matching just "dance" when we have dance variants like "dance_female", "dance_male"
            // These are ambiguous - they could be any dance emote, not just the base /dance
            if (emoteName == "dance" && text.Contains("dance_"))
                continue;

            if (text.Contains(emoteName.ToLowerInvariant()))
                return emoteName;
        }

        return "";
    }

    /// <summary>
    /// Generate candidate emote names from a filename.
    /// Tries various transformations to find a matching emote.
    /// </summary>
    private List<string> GenerateEmoteCandidates(string filename)
    {
        var candidates = new List<string>();

        // 1. Try the full filename first (e.g., "dance_male_loop")
        candidates.Add(filename);

        // 2. Remove common animation suffixes
        var cleaned = RemoveAnimationSuffixes(filename);
        if (!string.IsNullOrEmpty(cleaned) && cleaned != filename)
            candidates.Add(cleaned);

        // 3. For seated/ground emotes with prefixes (s_, j_, u_, l_)
        if (cleaned.StartsWith("s_") || cleaned.StartsWith("j_") ||
            cleaned.StartsWith("u_") || cleaned.StartsWith("l_"))
        {
            var withoutPrefix = cleaned.Substring(2);
            candidates.Add(withoutPrefix);
        }

        // 4. Try with underscores replaced
        var noUnderscores = cleaned.Replace("_", "");
        if (noUnderscores != cleaned)
            candidates.Add(noUnderscores);

        // 5. For dance variants (dance_male, dance_female, dance_elezen, etc.)
        // DON'T add these as candidates - they're ambiguous and could be ANY dance emote
        // Let GetChangedItems tell us which emote is actually affected
        // (We removed dance_male, dance_female, etc. from EmoteToCommand for this reason)

        // 6. For numbered dances (dance02, dance03, dance04, etc.)
        if (cleaned.StartsWith("dance") && cleaned.Length > 5 && char.IsDigit(cleaned[5]))
        {
            candidates.Add(cleaned); // e.g., dance04
        }

        return candidates;
    }

    private string RemoveAnimationSuffixes(string name)
    {
        // Remove standard suffixes
        var suffixes = new[] { "_loop", "_start", "_end", "_sp", "_idle", "_base", "_st" };
        bool changed;
        do
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (name.EndsWith(suffix))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return name.TrimEnd('_');
    }

    public string GetEmoteCommand(string emoteName)
    {
        if (string.IsNullOrEmpty(emoteName))
            return "";

        // ONLY return known commands - don't make up fake ones
        if (EmoteToCommand.TryGetValue(emoteName, out var command))
            return command;

        return "";
    }

    public void OnModAdded(string modDirectory)
    {
        var modList = penumbraService.GetModList();
        if (modList != null && modList.TryGetValue(modDirectory, out var modName))
        {
            var emoteInfo = AnalyzeModInternal(modDirectory, modName);
            cache.UpdateEntry(modDirectory, modName, emoteInfo);
            cache.SaveToDisk();
            log.Information($"[EmoteDetection] Added mod '{modName}' to cache");
        }
    }

    public void OnModDeleted(string modDirectory)
    {
        cache.RemoveEntry(modDirectory);
        cache.SaveToDisk();
        log.Information($"[EmoteDetection] Removed mod '{modDirectory}' from cache");
    }

    public void OnModMoved(string oldDirectory, string newDirectory)
    {
        var cached = cache.GetCachedEntry(oldDirectory);
        if (cached != null)
        {
            cache.RemoveEntry(oldDirectory);
            cache.UpdateEntry(newDirectory, cached.Name, cached.IsEmoteMod ? new EmoteModInfo
            {
                ModDirectory = newDirectory,
                ModName = cached.Name,
                AffectedEmotes = cached.AffectedEmotes,
                EmoteCommands = cached.EmoteCommands,
                PrimaryEmote = cached.PrimaryEmote,
                EmoteCommand = cached.PrimaryCommand,
                AnimationType = (AnimationType)cached.AnimationType,
                PoseIndex = cached.PoseIndex,
                AffectedPoseIndices = new List<int>(cached.AffectedPoseIndices ?? new List<int>())
            } : null);
            cache.SaveToDisk();
            log.Information($"[EmoteDetection] Moved mod in cache: {oldDirectory} -> {newDirectory}");
        }
    }

    public void ClearCacheAndRescan()
    {
        // Increment generation to cancel any ongoing scan
        // The running scan will see the generation mismatch and exit
        if (isInitializing)
        {
            log.Information("[EmoteDetection] Cancelling ongoing scan for rescan...");
            scanGeneration++;  // This will cause the old scan to abort
        }

        cache.ClearCache();
        log.Information("[EmoteDetection] Cache cleared, starting full rescan...");
        InitializeCacheAsync(force: true);
    }
}

public class EmoteModInfo
{
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public List<string> AffectedEmotes { get; set; } = new();
    public List<string> EmoteCommands { get; set; } = new();
    public List<string> EmotePaths { get; set; } = new();
    public string PrimaryEmote { get; set; } = "";
    public string EmoteCommand { get; set; } = "";

    public EmoteDetectionService.AnimationType AnimationType { get; set; } = EmoteDetectionService.AnimationType.Emote;

    /// <summary>
    /// The pose index (0-6) for pose mods, or -1 if not applicable/unknown.
    /// For multi-pose mods, this is the lowest index.
    /// </summary>
    public int PoseIndex { get; set; } = -1;

    /// <summary>
    /// All pose indices this mod affects (e.g., a mod replacing both groundsit 1 and 2).
    /// Empty for non-pose mods.
    /// </summary>
    public List<int> AffectedPoseIndices { get; set; } = new();

    public bool RequiresRedraw => AnimationType switch
    {
        EmoteDetectionService.AnimationType.StandingIdle => true,
        EmoteDetectionService.AnimationType.ChairSitting => true,
        EmoteDetectionService.AnimationType.GroundSitting => true,
        EmoteDetectionService.AnimationType.LyingDozing => true,
        _ => false
    };
}
