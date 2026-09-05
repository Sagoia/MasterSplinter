using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// <c>GitRepository.ReadLog</c> -- the host half of the commit list, now that record parsing
/// lives in <c>Parse/LogParser.cpp</c> (covered by the LogRecords suite in
/// <c>parse_test.cpp</c>).
/// <para>
/// What is pinned here is the wire contract: the badge tag must map onto
/// <see cref="BadgeKind"/> in declaration order, parents must come back as an array, and the
/// timestamp must be rebuilt from seconds plus a minute offset.
/// </para>
/// </summary>
public class LogUnpackTests
{
    private const ushort KindLog = 1;
    private const uint RecordSize = 104;

    private const int OffAuthorTime = 0;
    private const int OffCommitTime = 8;
    private const int OffAuthorTz = 16;
    private const int OffCommitTz = 20;
    private const int OffFullHash = 24;
    private const int OffShortHash = 32;
    private const int OffParents = 40;
    private const int OffAuthorName = 48;
    private const int OffAuthorEmail = 56;
    private const int OffCommitterName = 64;
    private const int OffCommitterEmail = 72;
    private const int OffBadges = 80;
    private const int OffSubject = 88;
    private const int OffBody = 96;

    /// <summary>A heap builder that hands back the off/len of everything it appends.</summary>
    private sealed class Heap
    {
        private readonly List<byte> _bytes = new();

        public (uint Off, uint Len) Add(string s)
        {
            if (s.Length == 0)
                return (0, 0);
            byte[] b = Encoding.UTF8.GetBytes(s);
            var at = ((uint)_bytes.Count, (uint)b.Length);
            _bytes.AddRange(b);
            return at;
        }

        private void Align4()
        {
            while (_bytes.Count % 4 != 0)
                _bytes.Add(0);
        }

        /// <summary>An array of string refs: 8 bytes each, 4-aligned.</summary>
        public (uint Off, uint Count) AddStringRefs(params string[] items)
        {
            if (items.Length == 0)
                return (0, 0);
            var refs = new List<(uint, uint)>();
            foreach (string s in items)
                refs.Add(Add(s));
            Align4();
            uint off = (uint)_bytes.Count;
            foreach ((uint o, uint l) in refs)
            {
                Put(o);
                Put(l);
            }
            return (off, (uint)items.Length);
        }

        /// <summary>An array of tagged string refs: 12 bytes each, 4-aligned.</summary>
        public (uint Off, uint Count) AddTagged(params (uint Tag, string Text)[] items)
        {
            if (items.Length == 0)
                return (0, 0);
            var refs = new List<(uint, uint, uint)>();
            foreach ((uint tag, string text) in items)
            {
                (uint o, uint l) = Add(text);
                refs.Add((tag, o, l));
            }
            Align4();
            uint off = (uint)_bytes.Count;
            foreach ((uint t, uint o, uint l) in refs)
            {
                Put(t);
                Put(o);
                Put(l);
            }
            return (off, (uint)items.Length);
        }

        private void Put(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _bytes.AddRange(b);
        }

        public byte[] Bytes => _bytes.ToArray();
    }

    private static void Ref(byte[] r, int at, (uint Off, uint Len) v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), v.Off);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 4), v.Len);
    }

    /// <summary>One packed commit record, laid out exactly as Parse/LogParser.h documents it.</summary>
    private static byte[] Record(long authorTime = 0, long commitTime = 0,
                                 int authorTz = 0, int commitTz = 0,
                                 (uint, uint) fullHash = default, (uint, uint) shortHash = default,
                                 (uint, uint) parents = default,
                                 (uint, uint) authorName = default, (uint, uint) authorEmail = default,
                                 (uint, uint) committerName = default, (uint, uint) committerEmail = default,
                                 (uint, uint) badges = default,
                                 (uint, uint) subject = default, (uint, uint) body = default)
    {
        var r = new byte[RecordSize];
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(OffAuthorTime), authorTime);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(OffCommitTime), commitTime);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(OffAuthorTz), authorTz);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(OffCommitTz), commitTz);
        Ref(r, OffFullHash, fullHash);
        Ref(r, OffShortHash, shortHash);
        Ref(r, OffParents, parents);
        Ref(r, OffAuthorName, authorName);
        Ref(r, OffAuthorEmail, authorEmail);
        Ref(r, OffCommitterName, committerName);
        Ref(r, OffCommitterEmail, committerEmail);
        Ref(r, OffBadges, badges);
        Ref(r, OffSubject, subject);
        Ref(r, OffBody, body);
        return r;
    }

    private static List<CommitRow> Read(byte[][] records, Heap heap)
        => GitRepository.ReadLog(PackedBuffer.Wrap(PackedTestBuffer.Build(
            kind: KindLog, recordSize: RecordSize, records: records, heap: heap.Bytes)));

    [Fact]
    public void EveryFieldLandsOnTheRow()
    {
        var h = new Heap();
        var record = Record(
            authorTime: 1767322995, commitTime: 1767326706, authorTz: 7 * 60, commitTz: 2 * 60,
            fullHash: h.Add("a1b2c3d4e5f6a7b8c9d0"), shortHash: h.Add("a1b2c3d"),
            authorName: h.Add("Alice"), authorEmail: h.Add("a@a"),
            committerName: h.Add("Bob"), committerEmail: h.Add("b@b"),
            subject: h.Add("Subject"), body: h.Add("Body text"));

        CommitRow row = Assert.Single(Read(new[] { record }, h));
        Assert.Equal("a1b2c3d4e5f6a7b8c9d0", row.FullHash);
        Assert.Equal("a1b2c3d", row.Hash);
        Assert.Equal("Alice", row.Author);
        Assert.Equal("a@a", row.AuthorEmail);
        Assert.Equal("Bob", row.Committer);
        Assert.Equal("b@b", row.CommitterEmail);
        Assert.Equal("Subject", row.Message);
        Assert.Equal("Body text", row.Body);
    }

    [Fact]
    public void TimestampsAreRebuiltFromSecondsPlusAMinuteOffset()
    {
        var h = new Heap();
        CommitRow row = Assert.Single(Read(new[]
        {
            Record(authorTime: 1767322995, commitTime: 1767326706, authorTz: 7 * 60, commitTz: -(5 * 60)),
        }, h));

        Assert.Equal(TimeSpan.FromHours(7), row.AuthorDate.Offset);
        Assert.Equal(1767322995, row.AuthorDate.ToUnixTimeSeconds());
        Assert.Equal(-TimeSpan.FromHours(5), row.CommitDate.Offset);
        Assert.Equal(1767326706, row.CommitDate.ToUnixTimeSeconds());
    }

    [Fact]
    public void ParentsComeBackAsAnArrayAndAsADisplayString()
    {
        var h = new Heap();
        var parents = h.AddStringRefs("1111111111", "2222222222");
        CommitRow row = Assert.Single(Read(new[] { Record(parents: parents) }, h));

        Assert.Equal(new[] { "1111111111", "2222222222" }, row.ParentHashes);
        // The display string abbreviates each to seven characters.
        Assert.Equal("1111111, 2222222", row.Parents);
    }

    [Fact]
    public void ARootCommitShowsAnEmDashRatherThanAnEmptyParentList()
    {
        var h = new Heap();
        CommitRow row = Assert.Single(Read(new[] { Record() }, h));

        Assert.Empty(row.ParentHashes);
        Assert.Equal("\u2014", row.Parents);
    }

    [Fact]
    public void TheBadgeTagMapsOntoTheHostEnumInDeclarationOrder()
    {
        // The kind travels as an integer, so this pins BadgeKind against LogParser.h's enum.
        // Reordering either without the other would silently recolour every decoration.
        var h = new Heap();
        var badges = h.AddTagged((0, "main"), (1, "origin/main"), (2, "v1.0"), (3, "HEAD"));
        CommitRow row = Assert.Single(Read(new[] { Record(badges: badges) }, h));

        Assert.Equal(4, row.Badges.Count);
        Assert.Equal(BadgeKind.LocalBranch, row.Badges[0].Kind);
        Assert.Equal("main", row.Badges[0].Text);
        Assert.Equal(BadgeKind.RemoteBranch, row.Badges[1].Kind);
        Assert.Equal(BadgeKind.Tag, row.Badges[2].Kind);
        Assert.Equal("v1.0", row.Badges[2].Text);
        Assert.Equal(BadgeKind.Head, row.Badges[3].Kind);
    }

    [Fact]
    public void NoDecorationsMeansNoBadges()
    {
        var h = new Heap();
        Assert.Empty(Assert.Single(Read(new[] { Record() }, h)).Badges);
    }

    [Fact]
    public void MessageTextCarryingSeparatorBytesSurvivesTheBoundary()
    {
        // The bug this format exists to kill, checked from the host side: both bytes are now
        // just content. Written as \u001f / \u001e, never \x1f -- hex escapes are greedy.
        var h = new Heap();
        string subject = "sub\u001fject";
        string body = "body\u001etail";
        CommitRow row = Assert.Single(Read(
            new[] { Record(subject: h.Add(subject), body: h.Add(body)) }, h));

        Assert.Equal(subject, row.Message);
        Assert.Equal(body, row.Body);
    }

    [Fact]
    public void SeveralRecordsBecomeSeveralRows()
    {
        var h = new Heap();
        var rows = Read(new[]
        {
            Record(subject: h.Add("first")),
            Record(subject: h.Add("second")),
            Record(subject: h.Add("third")),
        }, h);

        Assert.Equal(new[] { "first", "second", "third" }, rows.ConvertAll(r => r.Message));
    }

    [Fact]
    public void AnEmptyBufferYieldsNoCommits()
        => Assert.Empty(Read(System.Array.Empty<byte[]>(), new Heap()));

    [Fact]
    public void AMalformedBufferYieldsNoCommitsRatherThanThrowing()
        => Assert.Empty(GitRepository.ReadLog(PackedBuffer.Empty));
}
