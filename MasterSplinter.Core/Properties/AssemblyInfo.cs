using System.Runtime.CompilerServices;

// The git output parsers are `internal`: they are implementation detail of GitRepository, not API
// the UI should reach for. They are also the most intricate pure logic in the codebase (combined
// @@@ diffs, porcelain blame's per-sha header dedup, the rename two-record pairing), so the test
// project gets to see them rather than testing them through a live git process.
[assembly: InternalsVisibleTo("MasterSplinter.Core.Tests")]
