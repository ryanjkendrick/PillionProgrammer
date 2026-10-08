using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;

namespace ProposalsSpike
{
    /// <summary>
    /// Canned, deterministic proposals keyed on what the user typed. See FINDINGS.md for the trigger table.
    /// </summary>
    internal static class SpikeScenarios
    {
        /// <summary>Markers that, when present on the caret line or the line above, make the spike want a proposal.</summary>
        public static readonly string[] Markers = { "//ml", "/*right*/", "/*rightml*/", "/*replace*/", "//vs", "//vs0", "//ind", "//two", "//noflags", "//isense" };

        internal sealed class Result
        {
            public string Scenario;
            public List<ProposalBase> Proposals = new List<ProposalBase>();
            public string Note;
        }

        public static bool LineHasMarker(VirtualSnapshotPoint caret)
        {
            ITextSnapshotLine line = caret.Position.GetContainingLine();
            if (ContainsMarker(line.GetText())) return true;
            if (line.LineNumber > 0 && ContainsMarker(line.Snapshot.GetLineFromLineNumber(line.LineNumber - 1).GetText())) return true;
            return false;
        }

        private static bool ContainsMarker(string text)
        {
            foreach (var m in Markers) if (text.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static Result Build(VirtualSnapshotPoint caret, CompletionState completionState, string newLine)
        {
            ITextSnapshot snapshot = caret.Position.Snapshot;
            ITextSnapshotLine line = caret.Position.GetContainingLine();
            string before = snapshot.GetText(line.Start, caret.Position - line.Start);
            string after = snapshot.GetText(caret.Position, line.End - caret.Position);
            string prev = line.LineNumber > 0 ? snapshot.GetLineFromLineNumber(line.LineNumber - 1).GetText() : string.Empty;
            string indent = LeadingWhitespace(line.GetText());
            var empty = new SnapshotSpan(caret.Position, 0);
            var r = new Result();

            // 5. IntelliSense coexistence: completion list is up with WriteLine selected.
            //    Put "//isense" on the line above so the spike's ProposalManager says "generate here"; otherwise the
            //    next manager in the chain (Copilot's) decides, and it declines while a completion list is open.
            if (completionState != null)
            {
                r.Scenario = "isense";
                if (completionState.SelectedItem != null && completionState.SelectedItem.StartsWith("WriteLine", StringComparison.Ordinal))
                {
                    // Attempt 1 (18.10: DismissedDueToInvalidProposal) inserted only the continuation "(...)" at the end of
                    // the applicable span. The proposal has to read as "rest of the selected item + continuation".
                    var at = new SnapshotSpan(completionState.ApplicableToSpan.End, 0);
                    string typed = completionState.ApplicableToSpan.GetText();
                    string rest = completionState.SelectedItem.StartsWith(typed, StringComparison.Ordinal)
                        ? completionState.SelectedItem.Substring(typed.Length)
                        : completionState.SelectedItem;
                    Add(r, "isense", new[] { new ProposedEdit(at, rest + "(\"spike: proposal after IntelliSense item\");") }, caret, completionState, ProposalFlags.SingleTabToAccept);
                }
                else
                {
                    r.Note = $"completion item {SpikeLog.Show(completionState.SelectedItem)} is not WriteLine; no proposal";
                }

                return r;
            }

            // 1. Multi-line proposal at end of line.
            if (before.EndsWith("//ml", StringComparison.Ordinal) && after.Trim().Length == 0)
            {
                string text = " multi-line ghost text" + newLine +
                              indent + "int first = 1;" + newLine +
                              indent + "int second = 2;" + newLine +
                              indent + "Console.WriteLine(first + second);";
                Add(r, "ml", new[] { new ProposedEdit(empty, text) }, caret, null, ProposalFlags.None);
                return r;
            }

            // 1b. Single-line control case.
            if (before.EndsWith("//noflags", StringComparison.Ordinal) && after.Trim().Length == 0)
            {
                Add(r, "noflags", new[] { new ProposedEdit(empty, " single line, ProposalFlags.None") }, caret, null, ProposalFlags.None);
                return r;
            }

            // 2. Insertion while there is existing text to the right of the caret.
            if (before.EndsWith("/*right*/", StringComparison.Ordinal) && after.Length > 0)
            {
                Add(r, "right", new[] { new ProposedEdit(empty, "\"spike: inserted before existing text\"") }, caret, null, ProposalFlags.None);
                return r;
            }

            // 2b. Multi-line insertion while text follows the caret.
            if (before.EndsWith("/*rightml*/", StringComparison.Ordinal) && after.Length > 0)
            {
                string text = "\"first line\" +" + newLine + indent + "    \"second line\"";
                Add(r, "rightml", new[] { new ProposedEdit(empty, text) }, caret, null, ProposalFlags.None);
                return r;
            }

            // 2c. Probe replacement edits: (a) a replacement starting AT the caret (expected invalid per the docs),
            //     (b) an insertion at the caret plus a non-zero-length replacement further right.
            if (before.EndsWith("/*replace*/", StringComparison.Ordinal) && after.Length > 0)
            {
                var restOfLine = new SnapshotSpan(caret.Position, line.End);
                bool atCaretValid = Proposal.ValidateEdits(new[] { new ProposedEdit(restOfLine, "\"replaced rest of line\");") }, caret, null);
                r.Note = $"ValidateEdits(replacement span starting at caret)={atCaretValid}";
                int semi = after.LastIndexOf(';');
                var edits = new List<ProposedEdit> { new ProposedEdit(empty, "\"spike\"") };
                if (semi >= 0)
                {
                    edits.Add(new ProposedEdit(new SnapshotSpan(caret.Position + semi, 1), "; // replaced by proposal"));
                }

                Add(r, "replace", edits, caret, null, ProposalFlags.None);
                return r;
            }

            // 3. Virtual space: empty line after a line ending in //vs (in C# the caret sits in virtual space after Enter).
            bool lineEmpty = line.Length == 0;
            if (lineEmpty && prev.TrimEnd().EndsWith("//vs", StringComparison.Ordinal))
            {
                // Materialise the virtual indentation in the replacement text.
                string pad = new string(' ', caret.VirtualSpaces);
                Add(r, "vs", new[] { new ProposedEdit(empty, pad + "int fromVirtualSpace = 1; // padded with " + caret.VirtualSpaces + " spaces") }, caret, null, ProposalFlags.None);
                r.Note = $"caret.IsInVirtualSpace={caret.IsInVirtualSpace} virtualSpaces={caret.VirtualSpaces}";
                return r;
            }

            if (lineEmpty && prev.TrimEnd().EndsWith("//vs0", StringComparison.Ordinal))
            {
                // No padding: see where VS renders and inserts text when the proposal starts in virtual space.
                Add(r, "vs0", new[] { new ProposedEdit(empty, "int fromVirtualSpaceNoPad = 0;") }, caret, null, ProposalFlags.None);
                r.Note = $"caret.IsInVirtualSpace={caret.IsInVirtualSpace} virtualSpaces={caret.VirtualSpaces}";
                return r;
            }

            // 3b. After real indentation whitespace on an otherwise empty line.
            if (!lineEmpty && line.GetText().Trim().Length == 0 && caret.Position == line.End && !caret.IsInVirtualSpace
                && prev.TrimEnd().EndsWith("//ind", StringComparison.Ordinal))
            {
                Add(r, "ind", new[] { new ProposedEdit(empty, "int afterIndent = 2;") }, caret, null, ProposalFlags.None);
                return r;
            }

            // 4. Two proposals, to see whether next/previous cycling works.
            if (before.EndsWith("//two", StringComparison.Ordinal) && after.Trim().Length == 0)
            {
                Add(r, "two-a", new[] { new ProposedEdit(empty, " first of two proposals") }, caret, null, ProposalFlags.None);
                Add(r, "two-b", new[] { new ProposedEdit(empty, " second of two proposals") }, caret, null, ProposalFlags.None);
                return r;
            }

            return r;
        }

        private static void Add(Result r, string id, IReadOnlyList<ProposedEdit> edits, VirtualSnapshotPoint caret, CompletionState cs, ProposalFlags flags)
        {
            r.Scenario = r.Scenario ?? id;
            ProposalBase p = Proposal.TryCreateProposal(
                description: "Pillion spike: " + id,
                edits: edits,
                caret: caret,
                completionState: cs,
                flags: flags,
                commitAction: null,
                proposalId: "spike:" + id + ":" + Guid.NewGuid().ToString("N").Substring(0, 8),
                acceptText: "to accept (spike)",
                nextText: "next (spike)");
            if (p == null)
            {
                SpikeLog.Write("SOURCE", $"TryCreateProposal returned null for {id} (ValidateEdits failed)");
                return;
            }

            r.Proposals.Add(p);
        }

        private static string LeadingWhitespace(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }
    }
}
