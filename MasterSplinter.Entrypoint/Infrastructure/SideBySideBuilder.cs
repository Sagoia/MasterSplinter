using System;
using System.Collections.Generic;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>
    /// Turns a unified-diff line list into paired old/new rows for the side-by-side view (DIFF-002).
    /// Within a change block, consecutive removed lines pair positionally with consecutive added
    /// lines; any leftover becomes a one-sided row with a filler on the other side.
    /// </summary>
    public static class SideBySideBuilder
    {
        public static List<DiffRow> Build(IReadOnlyList<DiffLine> lines, string languageId)
        {
            var rows = new List<DiffRow>();
            int i = 0;
            while (i < lines.Count)
            {
                DiffLine line = lines[i];
                switch (line.Kind)
                {
                    case DiffLineKind.Hunk:
                        rows.Add(new DiffRow { IsHunk = true, HunkText = line.Text });
                        i++;
                        break;

                    case DiffLineKind.Context:
                        rows.Add(new DiffRow
                        {
                            Left = Cell(true, line.OldNo, line.Text, DiffLineKind.Context, languageId),
                            Right = Cell(true, line.NewNo, line.Text, DiffLineKind.Context, languageId),
                        });
                        i++;
                        break;

                    case DiffLineKind.Removed:
                    case DiffLineKind.Added:
                    {
                        var removed = new List<DiffLine>();
                        var added = new List<DiffLine>();
                        while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) { removed.Add(lines[i]); i++; }
                        while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) { added.Add(lines[i]); i++; }

                        int n = Math.Max(removed.Count, added.Count);
                        for (int k = 0; k < n; k++)
                        {
                            DiffCell left = k < removed.Count
                                ? Cell(true, removed[k].OldNo, removed[k].Text, DiffLineKind.Removed, languageId)
                                : new DiffCell();
                            DiffCell right = k < added.Count
                                ? Cell(true, added[k].NewNo, added[k].Text, DiffLineKind.Added, languageId)
                                : new DiffCell();
                            rows.Add(new DiffRow { Left = left, Right = right });
                        }
                        break;
                    }

                    default:
                        i++;
                        break;
                }
            }
            return rows;
        }

        private static DiffCell Cell(bool present, string no, string text, DiffLineKind kind, string languageId)
            => new() { Present = present, No = no, Text = text, Kind = kind, LanguageId = languageId };
    }
}
