using System.Reflection;
using System.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Rulesets.Configuration;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Updater;

public sealed class UpdaterRuleset : Ruleset
{
    public override string Description => "Zankai Updater";
    public override string ShortName => "zankai-updater";

    // Explicitly known generations. Never claim support for an arbitrary future API.
    public override string RulesetAPIVersionSupported
    {
        get
        {
            string? runtimeApi = typeof(Ruleset).GetField("CURRENT_RULESET_API_VERSION", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;
            return runtimeApi is "2022.822.0" or "2026.818.0" ? runtimeApi : "unsupported-updater-api";
        }
    }

    public override IEnumerable<Mod> GetModsFor(ModType type) => Array.Empty<Mod>();

    public override IRulesetConfigManager? CreateConfig(SettingsStore? settings)
    {
        if (settings != null) HelperLauncher.StartOnce();
        return null;
    }

    public override IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap) => new UpdaterConverter(beatmap);
    public override DifficultyCalculator CreateDifficultyCalculator(IWorkingBeatmap beatmap) => throw new BeatmapInvalidForRulesetException("The updater does not play beatmaps.");
    public override DrawableRuleset CreateDrawableRulesetWith(IBeatmap beatmap, IReadOnlyList<Mod>? mods = null) => throw new BeatmapInvalidForRulesetException("The updater does not play beatmaps.");

    private sealed class UpdaterConverter(IBeatmap beatmap) : IBeatmapConverter
    {
        public event Action<HitObject, IEnumerable<HitObject>> ObjectConverted { add { } remove { } }
        public IBeatmap Beatmap => beatmap;
        public bool CanConvert() => false;
        // lazer runs a conversion probe during ruleset registration, independently of CanConvert().
        public IBeatmap Convert(CancellationToken cancellationToken = default) => new Beatmap();
    }
}
