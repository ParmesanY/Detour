namespace ServiceLib.Tests.Manager;

public class RuleServerListTests
{
    private static readonly List<ServiceServerResolver.Candidate> All =
    [
        new("main", "[VL] Финляндия | YT без рекл.", "s1", 136),
        new("de", "[VL] Германия | YT без рекл.", "s1", -1),
        new("de2", "[VLx ДАБ] Германия | YT без рекл", "s1", 151),
    ];

    [Test]
    public async Task Parser_ServerList()
    {
        var rule = ConnectionRuleParser.Parse("@youtube +vpn = [VL] Германия | YT без рекл.;  ДАБ] Германия ; ", out _);
        await Assert.That(rule!.Server).IsEqualTo("[VL] Германия | YT без рекл.; ДАБ] Германия");
        await Assert.That(ConnectionRuleParser.Parse("@youtube +vpn = Германия; авто", out var err)).IsNull();
        await Assert.That(err).IsNotNull();
        await Assert.That(ConnectionRuleParser.Parse("@youtube +vpn = ;", out _)).IsNull();
    }

    [Test]
    public async Task FindAll_InOrder_NoRepeats()
    {
        var found = ServiceServerResolver.FindAll(["[VL] Германия", "ДАБ] Германия", "Нет такого", "[VL] Германия | YT без рекл."], "s1", All);
        await Assert.That(found.Select(c => c.IndexId)).IsEquivalentTo(new[] { "de", "de2" });
        await Assert.That(ServiceServerResolver.IsList("server:A; B")).IsTrue();
        await Assert.That(ServiceServerResolver.IsList("server:A")).IsFalse();
    }

    [Test]
    public async Task PickFirstAlive_SkipsDead()
    {
        await Assert.That(ServiceServerResolver.PickFirstAlive(["[VL] Германия", "ДАБ] Германия"], "main", "s1", All)).IsEqualTo("de2");
        await Assert.That(ServiceServerResolver.PickFirstAlive(["Финляндия", "ДАБ] Германия"], "main", "s1", All)).IsNull(); // main first
    }

    [Test]
    public async Task Check_SwitchesAfterTwoFailures_FromTop_NeverBackWhileAlive()
    {
        var alive = new HashSet<string> { "b", "c" };
        var selected = new List<string>();
        Task<bool> Probe(string m) => Task.FromResult(alive.Contains(m));
        Task Select(string s, string m) { selected.Add(m); return Task.CompletedTask; }
        var list = new RuleServerFailover.ListState("servers: a; b; c", ["a", "b", "c"]);

        await Assert.That(await RuleServerFailover.CheckAsync(list, Probe, Select)).IsNull(); // 1st failure
        await Assert.That(await RuleServerFailover.CheckAsync(list, Probe, Select)).IsEqualTo("b");
        await Assert.That(list.Current).IsEqualTo(1);

        alive.Add("a"); // "a" is back, "b" still works: stay
        await Assert.That(await RuleServerFailover.CheckAsync(list, Probe, Select)).IsNull();

        alive.Remove("b"); // "b" dies: back to the top, "a"
        await RuleServerFailover.CheckAsync(list, Probe, Select);
        await Assert.That(await RuleServerFailover.CheckAsync(list, Probe, Select)).IsEqualTo("a");
        await Assert.That(selected).IsEquivalentTo(new[] { "b", "a" });

        alive.Clear(); // all down: stays
        await RuleServerFailover.CheckAsync(list, Probe, Select);
        await Assert.That(await RuleServerFailover.CheckAsync(list, Probe, Select)).IsNull();
        await Assert.That(list.Current).IsEqualTo(0);
    }
}
