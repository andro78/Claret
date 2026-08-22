using System;
using System.Collections.Generic;
using System.Linq;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>One trigger that just matched, and the line it matched in.</summary>
    internal readonly record struct TriggerHit(TriggerRule Rule, string Line);

    /// <summary>
    /// Watches a session's output for trigger text. Runs on the read thread, so it stays to string
    /// searching over a small window: a few literals against the bytes that just arrived.
    /// </summary>
    internal sealed class TriggerWatcher
    {
        /// <summary>
        /// How much of the previous chunk to keep. A match can straddle a read — a board prints
        /// half a word, the rest arrives 20 ms later — so the tail of what was scanned is prefixed
        /// to the next chunk. Grown to fit the longest pattern in the list.
        /// </summary>
        private const int MinimumCarry = 128;

        /// <summary>
        /// Quiet time before the same trigger can fire again. Output arrives in bursts and the word
        /// being waited for is often in every line of one — a beep or a reply per line would be a
        /// fault, not a feature.
        /// </summary>
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

        private readonly object _gate = new();
        private readonly TextStream _text = new();
        private readonly Dictionary<string, DateTime> _lastFired = new(StringComparer.Ordinal);
        private readonly HashSet<string> _spent = new(StringComparer.Ordinal);

        private List<TriggerRule> _rules = new();
        private string _carry = string.Empty;
        private int _carryLimit = MinimumCarry;

        /// <summary>
        /// Replaces the rule list. Rules are matched by <see cref="TriggerRule.Key"/>, so a rule
        /// that has already fired its one shot stays fired across a re-push of the list, while one
        /// that was actually edited counts as new.
        /// </summary>
        public void Apply(IEnumerable<TriggerRule> rules)
        {
            List<TriggerRule> usable = rules.Where(rule => rule.IsUsable).Select(rule => rule.Clone()).ToList();

            lock (_gate)
            {
                _rules = usable;
                _carryLimit = usable.Count == 0
                    ? MinimumCarry
                    : Math.Max(MinimumCarry, usable.Max(rule => rule.Pattern.Length));

                var live = new HashSet<string>(usable.Select(rule => rule.Key), StringComparer.Ordinal);
                _spent.RemoveWhere(key => !live.Contains(key));

                foreach (string gone in _lastFired.Keys.Where(key => !live.Contains(key)).ToList())
                {
                    _lastFired.Remove(gone);
                }

                if (usable.Count == 0)
                {
                    _carry = string.Empty;
                }
            }
        }

        /// <summary>Forgets what has already fired, so one-shots arm again. For a reconnect.</summary>
        public void Rearm()
        {
            lock (_gate)
            {
                _spent.Clear();
                _lastFired.Clear();
                _carry = string.Empty;
            }
        }

        /// <summary>
        /// Feeds one chunk of output through and returns whatever fired, in rule order. Never more
        /// than one hit per rule per chunk: a burst is one event to the person watching it.
        /// </summary>
        public List<TriggerHit> Scan(byte[] data)
        {
            var hits = new List<TriggerHit>();

            lock (_gate)
            {
                if (_rules.Count == 0 || data.Length == 0)
                {
                    return hits;
                }

                string arrived = _text.Read(data);
                if (arrived.Length == 0)
                {
                    return hits;
                }

                string window = _carry + arrived;
                int fresh = _carry.Length;
                DateTime now = DateTime.UtcNow;

                foreach (TriggerRule rule in _rules)
                {
                    if (_spent.Contains(rule.Key))
                    {
                        continue;
                    }

                    if (_lastFired.TryGetValue(rule.Key, out DateTime last) && now - last < Cooldown)
                    {
                        continue;
                    }

                    int at = FindFresh(window, rule, fresh);
                    if (at < 0)
                    {
                        continue;
                    }

                    _lastFired[rule.Key] = now;
                    if (rule.Once)
                    {
                        _spent.Add(rule.Key);
                    }

                    hits.Add(new TriggerHit(rule, LineAt(window, at)));
                }

                _carry = window.Length <= _carryLimit ? window : window.Substring(window.Length - _carryLimit);
            }

            return hits;
        }

        /// <summary>
        /// First occurrence that ends inside the text that just arrived. A match ending in the
        /// carried tail was already reported when that tail was the new text, so requiring the end
        /// to be fresh counts each occurrence exactly once without remembering positions.
        /// </summary>
        private static int FindFresh(string window, TriggerRule rule, int fresh)
        {
            StringComparison comparison = rule.IgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            int from = Math.Max(0, fresh - rule.Pattern.Length + 1);

            while (from <= window.Length - rule.Pattern.Length)
            {
                int at = window.IndexOf(rule.Pattern, from, comparison);
                if (at < 0)
                {
                    return -1;
                }

                if (at + rule.Pattern.Length > fresh)
                {
                    return at;
                }

                from = at + 1;
            }

            return -1;
        }

        /// <summary>The line the match sits in, for the notice that names what fired.</summary>
        private static string LineAt(string window, int at)
        {
            int start = window.LastIndexOf('\n', Math.Min(at, window.Length - 1));
            int end = window.IndexOf('\n', at);

            string line = end < 0 ? window.Substring(start + 1) : window.Substring(start + 1, end - start - 1);
            return line.Trim();
        }
    }
}
