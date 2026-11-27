using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Versioning;

namespace osu.Game.Utils
{
    // Stands in for osu!'s marker of an official build. The updater recognises it by its full name alone.
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class OfficialBuildAttribute : Attribute
    {
    }
}

namespace RulesetUpdater.Tests
{
    // Writes the two client assemblies the updater inspects, so tests need no osu! installation.
    internal static class ClientFixture
    {
        public const string RulesetApi = "2026.818.0";
        public const string TargetFramework = ".NETCoreApp,Version=v10.0";
        public static readonly Version GameVersion = new(2026, 1115, 0, 0);

        // Returns the path of the entry assembly, as the bridge records it in a session.
        public static string Write(string directory, string version)
        {
            Directory.CreateDirectory(directory);

            var game = new PersistedAssemblyBuilder(new AssemblyName("osu.Game") { Version = GameVersion }, typeof(object).Assembly);
            TypeBuilder ruleset = game.DefineDynamicModule("osu.Game").DefineType("osu.Game.Rulesets.Ruleset", TypeAttributes.Public | TypeAttributes.Abstract);
            ruleset.DefineField("CURRENT_RULESET_API_VERSION", typeof(string), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal)
                   .SetConstant(RulesetApi);
            ruleset.CreateType();
            game.SetCustomAttribute(text<TargetFrameworkAttribute>(TargetFramework));
            game.Save(Path.Combine(directory, "osu.Game.dll"));

            var entry = new PersistedAssemblyBuilder(new AssemblyName("osu!"), typeof(object).Assembly);
            entry.DefineDynamicModule("osu!");
            entry.SetCustomAttribute(new CustomAttributeBuilder(typeof(osu.Game.Utils.OfficialBuildAttribute).GetConstructor(Type.EmptyTypes)!, []));
            entry.SetCustomAttribute(text<AssemblyInformationalVersionAttribute>(version));
            string path = Path.Combine(directory, "osu!.dll");
            entry.Save(path);
            return path;
        }

        private static CustomAttributeBuilder text<T>(string value) where T : Attribute =>
            new(typeof(T).GetConstructor([typeof(string)])!, [value]);
    }
}
