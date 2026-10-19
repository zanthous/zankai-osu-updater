using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class ExecutablePathTests
{
    private string temporary = null!;
    private string executable = null!;

    [SetUp]
    public void SetUp()
    {
        temporary = Path.Combine(Path.GetTempPath(), "zankai-path-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(temporary, "physical", "nested");
        Directory.CreateDirectory(directory);
        executable = Path.Combine(directory, SelfUpdate.HelperName);
        File.WriteAllText(executable, "helper fixture");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(temporary, true);

    [Test]
    public void AcceptsSameRegularFile() => Assert.That(ExecutablePath.IsExpected(executable, executable), Is.True);

    [Test]
    public void RejectsIdenticalCopyAtDifferentLocation()
    {
        string other = Path.Combine(temporary, SelfUpdate.HelperName);
        File.Copy(executable, other);
        Assert.That(ExecutablePath.IsExpected(other, executable), Is.False);
    }

    [Test]
    public void RejectsMissingFileOrProcess()
    {
        Assert.That(ExecutablePath.IsExpected(null, executable), Is.False);
        Assert.That(ExecutablePath.IsExpected(executable, Path.Combine(temporary, "missing")), Is.False);
    }

    [Test]
    public void AcceptsParentDirectoryAliasesInEitherDirection()
    {
        requireSymlinks();
        string alias = Path.Combine(temporary, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(temporary, "physical"));
        string other = Path.Combine(alias, "nested", SelfUpdate.HelperName);
        Assert.That(ExecutablePath.IsExpected(other, executable), Is.True);
        Assert.That(ExecutablePath.IsExpected(executable, other), Is.True);
    }

    [Test]
    public void AcceptsChainedRelativeDirectoryAliases()
    {
        requireSymlinks();
        Directory.CreateSymbolicLink(Path.Combine(temporary, "alias"), "physical");
        Directory.CreateSymbolicLink(Path.Combine(temporary, "alias-again"), "alias");
        Assert.That(ExecutablePath.IsExpected(Path.Combine(temporary, "alias-again", "nested", SelfUpdate.HelperName), executable), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AcceptsDirectoryAliasWhoseTargetHasAnAliasedParent(bool relativeTarget)
    {
        requireSymlinks();
        string parentAlias = Path.Combine(temporary, "parent-alias");
        Directory.CreateSymbolicLink(parentAlias, Path.Combine(temporary, "physical"));
        string alias = Path.Combine(temporary, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(relativeTarget ? "parent-alias" : parentAlias, "nested"));
        string other = Path.Combine(alias, SelfUpdate.HelperName);
        Assert.That(File.Exists(other), Is.True);
        Assert.That(ExecutablePath.IsExpected(other, executable), Is.True);
        Assert.That(ExecutablePath.IsExpected(executable, other), Is.True);
    }

    [Test]
    public void StillRejectsExecutableSymlinks()
    {
        requireSymlinks();
        string linked = Path.Combine(temporary, SelfUpdate.HelperName);
        File.CreateSymbolicLink(linked, executable);
        Assert.Throws<IOException>(() => ExecutablePath.IsExpected(executable, linked));
        Assert.Throws<IOException>(() => ExecutablePath.IsExpected(linked, executable));
    }

    private static void requireSymlinks()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Creating symlinks on Windows requires additional privileges.");
    }
}
