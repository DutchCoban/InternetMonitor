using System.Net;
using System.Net.Sockets;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// Step 6's chain over substituted probes: the adapter's own servers, then the public resolvers
/// one at a time, then the user's last-resort servers, then Windows' own resolver - and what each
/// outcome is called.
/// </summary>
[Collection("Localization")]
public sealed class DnsCheckTests
{
    private const string Hostname = "www.example.test";

    private static readonly IPAddress OwnServer = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress Quad9 = IPAddress.Parse("9.9.9.9");
    private static readonly IPAddress Cloudflare = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress Custom = IPAddress.Parse("10.0.0.53");
    private static readonly IPAddress Resolved = IPAddress.Parse("192.0.2.10");

    private static readonly DnsQueryResult Silent = new(false, -1, [], [], 1000, "Timed out");
    private static readonly DnsQueryResult NxDomain = new(true, 3, [], [], 12, null);

    private static DnsQueryResult Answer(double ms = 20) => new(true, 0, [Resolved], [], ms, null);

    /// <summary>The outside world as a table: which server answers what, which is reachable on 443, and what Windows says.</summary>
    private sealed class World
    {
        public Dictionary<IPAddress, DnsQueryResult> Answers { get; } = [];

        public HashSet<IPAddress> ReachableOn443 { get; } = [];

        public DnsOutcome System { get; set; } = new([], 30, SocketError.HostNotFound, false);

        public List<IPAddress> AskedOneByOne { get; } = [];

        public DnsProbes Probes => new(
            (servers, _, _, _, _) =>
            {
                IPAddress? winner = servers.FirstOrDefault(s => Answers.TryGetValue(s, out DnsQueryResult? r) && r.Success && r.Addresses.Count > 0);
                if (winner is not null)
                {
                    return Task.FromResult<(IPAddress?, DnsQueryResult)>((winner, Answers[winner]));
                }

                DnsQueryResult last = servers.Count == 0 ? new DnsQueryResult(false, -1, [], [], 0, "No servers") : Answers.GetValueOrDefault(servers[^1], Silent);
                return Task.FromResult<(IPAddress?, DnsQueryResult)>((null, last));
            },
            (server, _, _, _, _) =>
            {
                AskedOneByOne.Add(server);
                return Task.FromResult(Answers.GetValueOrDefault(server, Silent));
            },
            (server, _, _, _, _) => Task.FromResult(ReachableOn443.Contains(server)
                ? new TcpOutcome(true, false, 5, null)
                : new TcpOutcome(false, false, null, SocketError.TimedOut)),
            (_, _, _) => Task.FromResult(System));
    }

    private static StepContext Context(IReadOnlyList<IPAddress>? own = null, IReadOnlyList<IPAddress>? custom = null, int warningMs = 300) =>
        new(new CheckTargets
        {
            DnsProbeHostname = Hostname,
            PublicResolvers = [Quad9, Cloudflare],
            CustomDnsServers = custom ?? [],
            LatencyWarningMs = warningMs,
        })
        {
            DnsServers = own ?? [OwnServer],
        };

    private static Task<StepResult> Run(World world, StepContext context) => new DnsCheck(world.Probes).RunAsync(context, CancellationToken.None);

    [Fact]
    public async Task The_adapters_own_server_answering_is_ok_and_asks_nobody_else()
    {
        var world = new World();
        world.Answers[OwnServer] = Answer(ms: 18);

        StepResult result = await Run(world, Context());

        Assert.Equal(StepState.Ok, result.State);
        Assert.Equal("step.dns.ok", result.HeadlineKey);
        Assert.Equal([Hostname, "18"], result.HeadlineArgs!.Cast<string>());
        Assert.Null(result.DetailKey);
        Assert.Equal(18, result.ChartValue);
        Assert.Empty(world.AskedOneByOne);

        var facts = (DnsFacts)result.Facts!;
        Assert.Equal([Resolved], facts.ResolvedAddresses);
        Assert.Null(facts.PublicResolversWorked);
        Assert.Contains(result.Measurements, m => m.Value == OwnServer.ToString());
        Assert.Contains(result.Measurements, m => m.Value == "18 ms");
    }

    [Fact]
    public async Task A_slow_answer_is_a_warning_with_the_slow_detail()
    {
        var world = new World();
        world.Answers[OwnServer] = Answer(ms: 450);

        StepResult result = await Run(world, Context(warningMs: 300));

        Assert.Equal(StepState.Warning, result.State);
        Assert.Equal("step.dns.slow", result.DetailKey);
    }

    [Fact]
    public async Task When_the_own_server_fails_the_first_public_resolver_that_answers_names_the_fault()
    {
        var world = new World();
        world.Answers[OwnServer] = NxDomain;
        world.Answers[Quad9] = Answer();
        world.Answers[Cloudflare] = Answer();

        StepResult result = await Run(world, Context(custom: [Custom]));

        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.dns.ownResolverFailed", result.HeadlineKey);
        Assert.Equal("step.dns.ownResolverFailed.detail", result.DetailKey);
        Assert.Equal([OwnServer.ToString(), Hostname, Quad9], result.DetailArgs!);
        Assert.Equal([Quad9], world.AskedOneByOne);
        Assert.True(((DnsFacts)result.Facts!).PublicResolversWorked);
        Assert.Null(((DnsFacts)result.Facts!).CustomServersWorked);
        Assert.Contains(result.Measurements, m => m.Label == Quad9.ToString());
    }

    [Fact]
    public async Task An_adapter_without_dns_servers_that_a_public_resolver_rescues_is_reported_as_no_servers()
    {
        var world = new World();
        world.Answers[Quad9] = Answer();

        StepResult result = await Run(world, Context(own: []));

        Assert.Equal("step.dns.noServers", result.HeadlineKey);
        Assert.Equal("step.dns.noServers.detail", result.DetailKey);
        Assert.Equal([Quad9], result.DetailArgs!);
        Assert.Contains(result.Measurements, m => m.Value == "-");
    }

    [Fact]
    public async Task Only_the_users_last_resort_server_answering_is_custom_only()
    {
        var world = new World();
        world.Answers[Custom] = Answer();

        StepResult result = await Run(world, Context(custom: [Custom]));

        Assert.Equal("step.dns.customOnly", result.HeadlineKey);
        Assert.Equal([OwnServer.ToString(), $"{Quad9}, {Cloudflare}", Custom], result.DetailArgs!);
        Assert.Equal([Quad9, Cloudflare, Custom], world.AskedOneByOne);
        var facts = (DnsFacts)result.Facts!;
        Assert.False(facts.PublicResolversWorked);
        Assert.True(facts.CustomServersWorked);
        Assert.Contains(result.Measurements, m => m.Label.StartsWith(Custom.ToString(), StringComparison.Ordinal) && m.Label.Contains('('));
    }

    [Fact]
    public async Task Public_resolvers_silent_on_dns_but_reachable_on_443_mean_port_53_is_blocked()
    {
        var world = new World();
        world.ReachableOn443.Add(Quad9);

        StepResult result = await Run(world, Context());

        Assert.Equal("step.dns.portBlocked", result.HeadlineKey);
        Assert.Equal([OwnServer.ToString(), $"{Quad9}, {Cloudflare}"], result.DetailArgs!);
        Assert.True(((DnsFacts)result.Facts!).DnsPortBlockedSuspected);
    }

    [Fact]
    public async Task Nothing_answering_plain_dns_while_windows_resolves_is_a_warning_not_an_outage()
    {
        var world = new World { System = new DnsOutcome([Resolved], 40, null, false) };

        StepResult result = await Run(world, Context());

        Assert.Equal(StepState.Warning, result.State);
        Assert.Equal("step.dns.systemOnly", result.HeadlineKey);
        Assert.Equal([OwnServer.ToString(), $"{Quad9}, {Cloudflare}", Hostname], result.DetailArgs!);
        Assert.Equal(40, result.ChartValue);
        var facts = (DnsFacts)result.Facts!;
        Assert.True(facts.SystemResolverWorked);
        Assert.Equal([Resolved], facts.ResolvedAddresses);
    }

    [Fact]
    public async Task Nothing_at_all_is_a_failure_that_lists_what_was_tried()
    {
        var world = new World();

        StepResult plain = await Run(world, Context());
        Assert.Equal(StepState.Failed, plain.State);
        Assert.Equal("step.dns.failed", plain.HeadlineKey);
        Assert.Equal([Hostname], plain.HeadlineArgs!);
        Assert.Equal("step.dns.failed.detail", plain.DetailKey);
        Assert.False(((DnsFacts)plain.Facts!).PublicResolversWorked);

        StepResult withCustom = await Run(world, Context(custom: [Custom]));
        Assert.Equal("step.dns.failed.detailCustom", withCustom.DetailKey);
        Assert.Equal(Custom.ToString(), withCustom.DetailArgs![2]);
        Assert.False(((DnsFacts)withCustom.Facts!).CustomServersWorked);
    }

    [Fact]
    public async Task A_public_resolver_the_adapter_already_uses_is_not_asked_twice()
    {
        var world = new World();

        await Run(world, Context(own: [Quad9]));

        Assert.Equal([Cloudflare], world.AskedOneByOne);
    }
}
