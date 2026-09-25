using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Thematic grouping of Steam store tags.
/// </summary>
/// <remarks>
/// Groups are declared by canonical English tag name rather than by numeric id: the ids are
/// resolved at runtime against the tag catalog Steam serves, so a renumbered or renamed tag
/// drops out of the group instead of silently filtering by the wrong thing. Every catalog tag
/// that no group claims lands in <see cref="OtherGroupKey"/>, so nothing is lost.
/// </remarks>
public static class SteamTagGroups
{
    /// <summary>Group that collects catalog tags no other group claims.</summary>
    public const string OtherGroupKey = "other";

    /// <summary>The group holding Steam's adult and graphic content tags.</summary>
    public const string AdultGroupKey = "adult";

    /// <summary>
    /// Steam tag ids for adult and graphic content, used to negate them outright when the
    /// person has adult content switched off in settings.
    /// </summary>
    public static readonly IReadOnlyList<int> AdultTagIds = [12095, 9130, 6650, 4345, 4667];

    /// <summary>
    /// A thematic group of store tags.
    /// </summary>
    /// <param name="Key">Stable identifier used for settings and resource lookup.</param>
    /// <param name="IconKey">Key of the <c>Icons.xaml</c> geometry to show in the header.</param>
    /// <param name="FallbackTitle">English title, used if no localized string exists.</param>
    /// <param name="VisibleCount">How many bubbles are shown before the search box takes over.</param>
    /// <param name="OpenByDefault">Whether the group starts expanded.</param>
    /// <param name="TagNames">Canonical English tag names, in display order.</param>
    public sealed record Definition(
        string Key,
        string IconKey,
        string FallbackTitle,
        int VisibleCount,
        bool OpenByDefault,
        IReadOnlyList<string> TagNames);

    /// <summary>All tag groups, in the order they appear in the filter panel.</summary>
    public static readonly IReadOnlyList<Definition> All =
    [
        new("genre", "IconGamepad", "Genre", 60, true,
        [
            "Action", "Adventure", "Casual", "RPG", "Strategy", "Simulation", "Shooter", "Puzzle",
            "Horror", "Platformer", "Survival", "Racing", "Sports", "Fighting", "Visual Novel",
            "Metroidvania", "Roguelike", "Roguelite", "Massively Multiplayer", "Deckbuilding",
            "Tower Defense", "4X", "MOBA", "Battle Royale", "Souls-like", "Beat 'em up", "Stealth",
            "Point & Click", "Idler", "Sandbox", "Card Game", "Rhythm", "Education", "Utilities",
            "Indie", "Free to Play", "Early Access", "Action RPG", "JRPG", "CRPG", "RTS", "MMORPG",
            "eSports", "Party-Based RPG", "Tactical RPG", "Turn-Based Strategy", "Turn-Based Tactics",
            "Bullet Hell", "Dungeon Crawler", "Hack and Slash", "Immersive Sim", "Interactive Fiction",
            "Life Sim", "City Builder", "Colony Sim", "Grand Strategy", "Auto Battler", "Shoot 'Em Up",
            "Twin Stick Shooter", "Looter Shooter", "Extraction Shooter", "Boomer Shooter", "Arena Shooter",
            "Party Game", "Hero Shooter", "Top-Down Shooter", "Card Battler", "Roguelike Deckbuilder",
            "Tabletop", "Board Game", "Wargame", "Real-Time Tactics", "God Game", "Vehicle Combat",
            "Space Sim", "Flight", "Naval Combat", "Combat Racing", "Kart", "Brawler", "Precision Platformer",
            "3D Platformer", "2D Platformer", "Dating Sim", "Otome", "Time Management", "Tycoon",
            "Management", "Business Sim", "Medical Sim", "Cooking", "Fishing", "Hunting", "Mining",
            "Clicker", "Incremental", "Match 3", "Trivia", "Golf", "Football (Soccer)", "Basketball"
        ]),

        new("setting", "IconGlobe", "Setting & theme", 60, true,
        [
            "Fantasy", "Sci-fi", "Anime", "Dark", "Post-apocalyptic", "Medieval", "Cyberpunk",
            "Space", "Open World", "Zombies", "Realistic", "Historical", "Military", "Mystery",
            "Steampunk", "Pirates", "Vikings", "Nature", "Cozy", "Lovecraftian", "Detective",
            "Dystopian", "Mythology", "Western", "Underwater", "Farming", "Cats", "Dogs",
            "Dragons", "Aliens", "Robots", "Demons", "Vampires", "Ninja", "Samurai", "Superhero",
            "Dinosaurs", "Horses", "Trains", "Automobile Sim", "War", "World War II", "Cold War",
            "Crime", "Conspiracy", "Noir", "Gothic", "Supernatural", "Time Travel", "Space Sim",
            "Dark Fantasy", "High Fantasy", "Urban Fantasy", "Cosmic Horror", "Post-Apocalyptic",
            "World War I", "Modern", "Futuristic", "Wild West", "Mafia", "Yakuza", "Mecha",
            "Kaiju", "Cyborg", "AI", "Wholesome", "Cute", "Animals", "Birds", "Forest", "Island",
            "Desert", "Jungle", "Mountain", "Arctic", "Snow", "Castle", "Ruins", "City",
            "Deep Space", "Outer Space", "Submarine", "Solarpunk", "Ancient", "Rome", "Egypt"
        ]),

        new("gameplay", "IconJoystick", "Gameplay", 60, false,
        [
            "Story Rich", "Exploration", "Combat", "Crafting", "Turn-Based Combat", "Real-Time",
            "Building", "Base Building", "Resource Management", "Procedural Generation",
            "Perma Death", "Physics", "Character Customization", "Loot", "Multiple Endings",
            "Choices Matter", "Level Editor", "Moddable", "Replay Value", "Automation", "Tactical",
            "Time Management", "Creature Collector", "Inventory Management", "Grid-Based Movement",
            "Class-Based", "Team-Based", "Trading", "Economy", "Investigation", "Puzzle Platformer",
            "Open World Survival Craft", "Roguelike Deckbuilder", "Narrative", "Dialogue Heavy",
            "PvE", "PvP", "Social Deduction", "Asynchronous Multiplayer", "Co-op Campaign",
            "Skill Tree", "Linear", "Branching Plot", "Lore-Rich", "Environmental Storytelling",
            "Logic", "Hidden Object", "Mini Games", "Real-Time Combat", "Tactical Combat",
            "Cover Shooter", "Melee", "Swordplay", "Martial Arts", "Magic", "Spellcasting",
            "Stealth", "Assassination", "Parkour", "Wall Running", "Grappling Hook", "Driving",
            "Drifting", "Sailing", "Swimming", "Climbing", "Pet Collection", "Monster Tamer",
            "Breeding", "Evolution", "Steam Workshop", "Customization", "Randomized", "Score Attack",
            "Time Attack", "Boss Rush", "Co-op", "Local Co-Op", "Online Co-Op", "Split Screen"
        ]),

        new("pace", "IconBolt", "Pace & difficulty", 60, false,
        [
            "Relaxing", "Difficult", "Fast-Paced", "Competitive", "Precision Platformer",
            "Time Attack", "Short", "Epic", "Emotional", "Funny", "Comedy", "Atmospheric",
            "Psychological", "Psychological Horror", "Survival Horror", "Jump Scare", "Addictive",
            "Score Attack", "Boss Rush", "Dark Humor", "Satire", "Wholesome", "Surreal",
            "Philosophical", "Great Soundtrack", "Beautiful", "Immersive", "Nostalgia",
            "Casual", "Tactical", "Strategic", "Mind-Bending", "Experimental", "Minimalist"
        ]),

        new("style", "IconPalette", "Visual style", 60, false,
        [
            "2D", "3D", "Pixel Graphics", "Stylized", "Colorful", "Cartoony", "Cartoon",
            "Minimalist", "Isometric", "Hand-drawn", "Voxel", "Retro", "2.5D", "Low-poly",
            "Psychedelic", "Cute", "First-Person", "Third Person", "Top-Down", "Side Scroller",
            "Third-Person Shooter", "Top-Down Shooter", "FPS", "Cinematic", "FMV", "Abstract",
            "Comic Book", "Anime", "Silent Protagonist", "Female Protagonist", "Villain Protagonist",
            "Realistic", "Dark", "Gothic", "Surreal", "Split Screen"
        ]),

        new(AdultGroupKey, "IconShield", "Adult content", 60, false,
        [
            "Sexual Content", "Hentai", "Nudity", "Gore", "Violent", "NSFW", "Mature"
        ])
    ];
}
