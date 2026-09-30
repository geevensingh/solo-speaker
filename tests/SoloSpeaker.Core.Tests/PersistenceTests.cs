using SoloSpeaker.Core.Identity;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.StateStore;
using SoloSpeaker.Core.Tests.Fakes;
using SoloSpeaker.Core.Tests.StateMachine;
using SoloSpeaker.Core.Tests.WireFormat;

namespace SoloSpeaker.Core.Tests;

/// <summary>
/// §7.5's persisted files: the atomic write, the strict parse, the bounds, and §4.5's two
/// state rows.
/// </summary>
public sealed class PersistenceTests
{
    private const string StatePath = @"C:\root\state.json";
    private const string ConfigPath = @"C:\root\config.json";

    private static PairId PairId => WireVectorConstants.PairId;

    private static MachineId Self => ReducerHarness.Id(ReducerHarness.AHex);

    private static MachineId Peer => ReducerHarness.Id(ReducerHarness.BHex);

    private static Roster CompleteRoster()
    {
        Assert.True(Roster.TryCreate(Self, Peer, out Roster? roster));
        return roster!;
    }

    private static (FakeFileStore Files, FakeSecretProtector Protector) Paired(Roster? roster = null)
    {
        var files = new FakeFileStore();
        var protector = new FakeSecretProtector();

        JsonConfigStore.Create(files, ConfigPath, protector, PairId, WireVectorConstants.TestPairKey, roster ?? CompleteRoster());

        var state = new JsonStateStore(files, StatePath, PairId);
        state.SaveState(MachineId.None, 0);

        return (files, protector);
    }

    // --- state.json ------------------------------------------------------------------

    [Fact]
    public void State_round_trips_through_the_canonical_rendering()
    {
        (FakeFileStore files, _) = Paired();
        var store = new JsonStateStore(files, StatePath, PairId);

        store.SaveState(Peer, 41);

        var reread = new JsonStateStore(files, StatePath, PairId);

        Assert.True(reread.TryLoadState(out MachineId owner, out ulong seq));
        Assert.Equal(Peer, owner);
        Assert.Equal(41UL, seq);
        Assert.Equal(PairId, reread.StatePairId);
    }

    [Fact]
    public void The_pre_claim_owner_round_trips_as_thirty_two_zeros()
    {
        (FakeFileStore files, _) = Paired();
        var store = new JsonStateStore(files, StatePath, PairId);

        store.SaveState(MachineId.None, 0);

        Assert.Contains($"\"{new string('0', 32)}\"", files.TextAt(StatePath), StringComparison.Ordinal);
        Assert.True(new JsonStateStore(files, StatePath, PairId).TryLoadState(out MachineId owner, out _));
        Assert.True(owner.IsNone);
    }

    /// <summary>§4.5: "temp file present but replace never happened -> previous state intact".</summary>
    [Fact]
    public void An_interrupted_write_leaves_the_previous_state_intact()
    {
        (FakeFileStore files, _) = Paired();
        var store = new JsonStateStore(files, StatePath, PairId);
        store.SaveState(Peer, 7);

        files.InterruptNextWrite = true;
        store.SaveState(Self, 8);

        Assert.True(files.PendingWriteExists(StatePath));

        var reread = new JsonStateStore(files, StatePath, PairId);

        Assert.True(reread.TryLoadState(out MachineId owner, out ulong seq));
        Assert.Equal(Peer, owner);
        Assert.Equal(7UL, seq);
        Assert.Equal(ErrorCause.None, reread.LastRead.Cause);

        // The orphan is cleaned up rather than raised - the main file is whole.
        Assert.False(files.PendingWriteExists(StatePath));
        Assert.Equal(1, files.DiscardCount);
    }

    /// <summary>
    /// §5.4 bounds <c>seq</c> on the wire because an unbounded one "poisons the persisted
    /// state on both machines". The file needs the same bound or the check is reachable
    /// around.
    /// </summary>
    [Fact]
    public void A_persisted_seq_beyond_the_bound_is_refused()
    {
        var files = new FakeFileStore();
        files.Seed(StatePath, $$"""
            { "schema": 1, "pairId": "{{PairId}}", "activeOwner": "{{Peer}}", "seq": {{ulong.MaxValue}} }
            """);

        var store = new JsonStateStore(files, StatePath, PairId);

        Assert.False(store.TryLoadState(out _, out _));
        Assert.Equal(ErrorCause.SeqBoundExceeded, store.LastRead.Cause);
    }

    [Fact]
    public void A_persisted_seq_at_the_bound_is_accepted()
    {
        var files = new FakeFileStore();
        files.Seed(StatePath, $$"""
            { "schema": 1, "pairId": "{{PairId}}", "activeOwner": "{{Peer}}", "seq": {{JsonStateStore.MaxPersistedSeq}} }
            """);

        Assert.True(new JsonStateStore(files, StatePath, PairId).TryLoadState(out _, out ulong seq));
        Assert.Equal(JsonStateStore.MaxPersistedSeq, seq);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "schema": 1, "pairId": "nope", "activeOwner": "2d81e407fa63b95c18204e7dc6395fa1", "seq": 1 }""")]
    [InlineData("""{ "schema": 1, "pairId": "b1f0a2c3d4e5f60718293a4b5c6d7e8f", "activeOwner": "NOTHEX", "seq": 1 }""")]
    [InlineData("""{ "schema": 1, "pairId": "00000000000000000000000000000000", "activeOwner": "2d81e407fa63b95c18204e7dc6395fa1", "seq": 1 }""")]
    public void A_malformed_state_file_is_refused_rather_than_coerced(string contents)
    {
        var files = new FakeFileStore();
        files.Seed(StatePath, contents);

        var store = new JsonStateStore(files, StatePath, PairId);

        Assert.False(store.TryLoadState(out _, out _));
        Assert.Equal(ErrorCause.StateUnreadable, store.LastRead.Cause);
    }

    /// <summary>An unrecognised schema is its own outcome, never a migration.</summary>
    [Fact]
    public void A_state_file_from_a_newer_build_is_refused_as_such()
    {
        var files = new FakeFileStore();
        files.Seed(StatePath, $$"""
            { "schema": 99, "pairId": "{{PairId}}", "activeOwner": "{{Peer}}", "seq": 1 }
            """);

        var store = new JsonStateStore(files, StatePath, PairId);

        Assert.False(store.TryLoadState(out _, out _));
        Assert.Equal(ErrorCause.PersistedSchemaUnknown, store.LastRead.Cause);
    }

    // --- config.json -----------------------------------------------------------------

    [Fact]
    public void Config_round_trips_including_the_protected_pair_key()
    {
        (FakeFileStore files, FakeSecretProtector protector) = Paired();

        Assert.True(JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config).IsUsable);
        Assert.Equal(PairId, config!.PairId);
        Assert.Equal(Self, config.Roster.Self);
        Assert.Equal(Peer, config.Roster.Peer);
        Assert.True(config.IsPaired);
        Assert.True(config.TryGetPairKey(out byte[] pairKey));
        Assert.Equal(WireVectorConstants.TestPairKey, pairKey);
    }

    /// <summary>ADR 0013: a decrypt failure raises error, never crashes and never falls back.</summary>
    [Fact]
    public void A_config_that_cannot_be_decrypted_is_refused_without_throwing()
    {
        (FakeFileStore files, FakeSecretProtector protector) = Paired();
        protector.RefuseEverything = true;

        PersistedReadResult read = JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config);

        Assert.Null(config);
        Assert.Equal(ErrorCause.ConfigUnreadable, read.Cause);
    }

    /// <summary>
    /// The published vector key must load, because the two-node harness signs with it. A
    /// denylist naming it would have made work item 4's restart scenario impossible.
    /// </summary>
    [Fact]
    public void The_published_test_key_is_accepted_because_the_harness_signs_with_it()
    {
        (FakeFileStore files, FakeSecretProtector protector) = Paired();

        Assert.True(JsonConfigStore.TryLoad(files, ConfigPath, protector, out _).IsUsable);
    }

    [Fact]
    public void A_structurally_unsound_pair_key_is_refused()
    {
        var files = new FakeFileStore();
        var protector = new FakeSecretProtector();

        JsonConfigStore.Create(files, ConfigPath, protector, PairId, new byte[32], CompleteRoster());

        PersistedReadResult read = JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config);

        Assert.Null(config);
        Assert.Equal(ErrorCause.PairKeyRefused, read.Cause);
    }

    [Fact]
    public void An_absent_config_is_not_an_error()
    {
        var files = new FakeFileStore();

        PersistedReadResult read = JsonConfigStore.TryLoad(files, ConfigPath, new FakeSecretProtector(), out JsonConfigStore? config);

        Assert.Null(config);
        Assert.False(read.Found);
        Assert.Equal(ErrorCause.None, read.Cause);
    }

    /// <summary>
    /// Goal 8 invites hand editing. A mistyped tunable falls back and names itself rather
    /// than taking the pair dark and telling the user to re-pair, which would not fix it.
    /// </summary>
    [Fact]
    public void A_bad_tunable_falls_back_to_its_default_and_names_the_field()
    {
        (FakeFileStore files, FakeSecretProtector protector) = Paired();
        string text = files.TextAt(ConfigPath).Replace("48292", "999999", StringComparison.Ordinal);
        files.Seed(ConfigPath, text);

        Assert.True(JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config).IsUsable);
        Assert.Equal(ConfigDefaults.Port, config!.Port);
        Assert.Equal(ErrorCause.TunableFellBackToDefault, config.TunableFault);
        Assert.Equal("Port", config.FaultedField);
    }

    [Theory]
    [InlineData("\"roster\": []")]
    [InlineData("\"roster\": [\"NOTHEX\"]")]
    [InlineData("\"roster\": [\"00000000000000000000000000000000\"]")]
    public void A_bad_identity_field_refuses_the_whole_document(string replacement)
    {
        (FakeFileStore files, FakeSecretProtector protector) = Paired();
        string text = files.TextAt(ConfigPath);
        int start = text.IndexOf("\"roster\"", StringComparison.Ordinal);
        int end = text.IndexOf(']', start) + 1;
        files.Seed(ConfigPath, string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end)));

        PersistedReadResult read = JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config);

        Assert.Null(config);
        Assert.Equal(ErrorCause.ConfigUnreadable, read.Cause);
    }

    /// <summary>
    /// The reason every §7.5 field is modelled: a rewrite that parsed only what it needed
    /// would silently delete the user's port and hotkey.
    /// </summary>
    [Fact]
    public void Completing_the_roster_preserves_every_other_field()
    {
        Assert.True(Roster.TryCreate(Self, null, out Roster? incomplete));
        (FakeFileStore files, FakeSecretProtector protector) = Paired(incomplete);

        string text = files.TextAt(ConfigPath)
            .Replace("48292", "50000", StringComparison.Ordinal)
            .Replace("\"denylist\": []", "\"denylist\": [\"nvidia-broadcast.exe\"]", StringComparison.Ordinal);
        files.Seed(ConfigPath, text);

        Assert.True(JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config).IsUsable);
        Assert.False(config!.IsPaired);

        Assert.True(config.TryCompleteRoster(Peer));

        Assert.True(config.IsPaired);
        Assert.Equal(Peer, config.Roster.Peer);

        string rewritten = files.TextAt(ConfigPath);
        Assert.Contains("50000", rewritten, StringComparison.Ordinal);
        Assert.Contains("nvidia-broadcast.exe", rewritten, StringComparison.Ordinal);
        Assert.Contains(Peer.ToString(), rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Completing_an_already_complete_roster_is_refused()
    {
        (FakeFileStore files, FakeSecretProtector protector) = Paired();

        Assert.True(JsonConfigStore.TryLoad(files, ConfigPath, protector, out JsonConfigStore? config).IsUsable);
        Assert.False(config!.TryCompleteRoster(ReducerHarness.Id(ReducerHarness.StrangerHex)));
    }

    // --- the cross-file check --------------------------------------------------------

    [Fact]
    public void Matching_pair_ids_start_from_the_persisted_state()
    {
        StartupOutcome outcome = StartupDecision.Decide(
            PairId, PersistedReadResult.Loaded, PairId, Peer, 41, TimeSpan.Zero);

        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.Equal(Peer, outcome.State.ActiveOwner);
        Assert.Equal(41UL, outcome.State.Seq);
    }

    /// <summary>§7.5: a half-restored pair raises error rather than being silently tolerated.</summary>
    [Fact]
    public void Mismatched_pair_ids_raise_rather_than_being_tolerated()
    {
        PairId other = WireVectorConstants.ParsePairId("00112233445566778899aabbccddeeff");

        StartupOutcome outcome = StartupDecision.Decide(
            PairId, PersistedReadResult.Loaded, other, Peer, 41, TimeSpan.Zero);

        Assert.Equal(ErrorCause.StatePairIdMismatch, outcome.Cause);
        Assert.True(outcome.State.ActiveOwner.IsNone);
    }

    /// <summary>
    /// §10: "delete `state.json` but not `config.json` and confirm `error` rather than
    /// silent misbehaviour". Only distinguishable because the ceremony writes state too.
    /// </summary>
    [Fact]
    public void A_deleted_state_file_on_a_configured_machine_raises()
    {
        StartupOutcome outcome = StartupDecision.Decide(
            PairId, PersistedReadResult.Absent, null, MachineId.None, 0, TimeSpan.Zero);

        Assert.Equal(ErrorCause.StateUnreadable, outcome.Cause);
    }

    [Fact]
    public void A_genuine_first_run_is_not_an_error()
    {
        StartupOutcome outcome = StartupDecision.Decide(
            null, PersistedReadResult.Absent, null, MachineId.None, 0, TimeSpan.Zero);

        Assert.Equal(ErrorCause.None, outcome.Cause);
        Assert.True(outcome.State.ActiveOwner.IsNone);
        Assert.Equal(0UL, outcome.State.Seq);
    }

    /// <summary>
    /// A stale state file surviving an uninstall that removed only the config. Without the
    /// ceremony writing state, this would instead surface as an error on the first start
    /// after a successful pairing.
    /// </summary>
    [Fact]
    public void State_without_configuration_is_reported_rather_than_acted_on()
    {
        StartupOutcome outcome = StartupDecision.Decide(
            null, PersistedReadResult.Loaded, PairId, Peer, 41, TimeSpan.Zero);

        Assert.Equal(ErrorCause.StateUnreadable, outcome.Cause);
        Assert.True(outcome.State.ActiveOwner.IsNone);
    }

    /// <summary>Every outcome leaves the machine audible - Goal 1 by construction.</summary>
    [Fact]
    public void No_startup_outcome_begins_from_a_state_that_could_mute()
    {
        PairId other = WireVectorConstants.ParsePairId("00112233445566778899aabbccddeeff");

        StartupOutcome[] outcomes =
        [
            StartupDecision.Decide(null, PersistedReadResult.Absent, null, MachineId.None, 0, TimeSpan.Zero),
            StartupDecision.Decide(PairId, PersistedReadResult.Absent, null, MachineId.None, 0, TimeSpan.Zero),
            StartupDecision.Decide(PairId, PersistedReadResult.Loaded, other, Peer, 41, TimeSpan.Zero),
            StartupDecision.Decide(PairId, PersistedReadResult.Refused(ErrorCause.StateUnreadable), null, Peer, 41, TimeSpan.Zero),
            StartupDecision.Decide(null, PersistedReadResult.Loaded, PairId, Peer, 41, TimeSpan.Zero),
        ];

        foreach (StartupOutcome outcome in outcomes)
        {
            // Presence is never carried across a restart, so nothing can mute before a peer
            // is heard from again - whatever the owner says.
            Assert.Null(outcome.State.PeerLastSeenAt);

            // Design revision 10: and every outcome is quarantined, which closes the
            // direction this test used to leave open. A quarantined machine broadcasts
            // nothing, so a cold start cannot put its persisted (activeOwner, seq) on the
            // wire and make the *peer* mute either. §5.5 also gates shouldMute on the window
            // being closed, so the local machine stays audible for its whole duration.
            Assert.NotNull(outcome.State.Quarantine);
        }
    }

    /// <summary>
    /// Design revision 10: cold-start silence is guaranteed by the StartupDecision result
    /// itself, not by the host remembering to enter quarantine before it starts the socket.
    /// </summary>
    [Fact]
    public void Every_startup_decision_outcome_opens_the_quarantine_window()
    {
        PairId other = WireVectorConstants.ParsePairId("00112233445566778899aabbccddeeff");

        StartupOutcome[] outcomes =
        [
            StartupDecision.Decide(null, PersistedReadResult.Absent, null, MachineId.None, 0, TimeSpan.Zero),
            StartupDecision.Decide(PairId, PersistedReadResult.Absent, null, MachineId.None, 0, TimeSpan.Zero),
            StartupDecision.Decide(PairId, PersistedReadResult.Loaded, other, Peer, 41, TimeSpan.Zero),
            StartupDecision.Decide(PairId, PersistedReadResult.Refused(ErrorCause.StateUnreadable), null, Peer, 41, TimeSpan.Zero),
            StartupDecision.Decide(null, PersistedReadResult.Loaded, PairId, Peer, 41, TimeSpan.Zero),
        ];

        foreach (StartupOutcome outcome in outcomes)
        {
            Assert.NotNull(outcome.State.Quarantine);
        }
    }
}
