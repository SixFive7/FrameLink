namespace FrameLink.Protocol;

/// <summary>
/// Debian version ordering — <c>dpkg --compare-versions</c>, in managed code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Package versions are only interesting once something can say
/// which of two is newer, and "newer" in Debian is not lexicographic and not numeric: the
/// ordinary string comparison puts <c>1.10</c> before <c>1.9</c>, and a naive numeric split
/// mishandles <c>1:146.0.7680.164-1~deb13u1+rpt1</c> in three separate ways. Both programs need
/// the answer — the agent to tell a security update apart from a package that moved
/// <i>backward</i>, the Fleet Manager to say "this frame is three versions ahead on chromium" —
/// so it lives here, in the project with no dependencies that both already share.
/// </para>
/// <para>
/// <b>Shelling out to <c>dpkg --compare-versions</c> was the alternative and is worse in both
/// places.</b> The Fleet Manager is an amd64 container with no dpkg at all, and on the frame it
/// would put a process launch inside a comparison that runs nine hundred times per report.
/// </para>
/// <para>
/// <b>The algorithm is dpkg's, transcribed rather than reinvented.</b> A version is
/// <c>[epoch:]upstream[-revision]</c>; epoch compares numerically, then upstream and revision
/// each compare under the rule below, which is <c>verrevcmp</c> from <c>dpkg/lib/dpkg/version.c</c>:
/// runs of non-digits compare character by character under a modified ordering where <c>~</c>
/// sorts before everything including the end of the string, letters sort before all other
/// punctuation, and end-of-string sorts before punctuation; runs of digits compare numerically
/// with leading zeros ignored. That is what makes <c>1.0~rc1</c> older than <c>1.0</c>, and
/// <c>1.0</c> older than <c>1.0a</c>.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A version string dpkg would reject is still a string some frame
/// has installed, and refusing to order it would take out a whole inventory comparison over one
/// entry. An unparseable epoch is treated as part of the upstream version, which is the
/// conservative reading: it can misorder that one package and can never fail a report.
/// </para>
/// </remarks>
public static class DebianVersion
{
    /// <summary>
    /// Orders two Debian version strings the way <c>dpkg --compare-versions</c> does.
    /// </summary>
    /// <param name="left">The first version. Null and empty compare as "no version at all".</param>
    /// <param name="right">The second version.</param>
    /// <returns>
    /// Negative when <paramref name="left"/> is older, zero when they are equal, positive when
    /// <paramref name="left"/> is newer.
    /// </returns>
    public static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);

        if (a.Epoch != b.Epoch)
        {
            return a.Epoch < b.Epoch ? -1 : 1;
        }

        var upstream = CompareFragment(a.Upstream, b.Upstream);
        return upstream != 0 ? upstream : CompareFragment(a.Revision, b.Revision);
    }

    /// <summary>Whether two version strings name the same version, epoch included.</summary>
    /// <remarks>
    /// Not <c>string.Equals</c>: <c>0:1.2-3</c> and <c>1.2-3</c> are the same version written two
    /// ways, and dpkg reports whichever form the maintainer used.
    /// </remarks>
    public static bool AreEqual(string? left, string? right) => Compare(left, right) == 0;

    /// <summary>Splits a version into its three comparable parts.</summary>
    /// <remarks>
    /// The revision is what follows the <b>last</b> hyphen, not the first: <c>0.7.0+rpt20260205-1</c>
    /// has one revision and <c>1.26.2-1+rpt3+deb13u1</c> has one too, while an upstream version
    /// containing hyphens keeps them.
    /// </remarks>
    private static (long Epoch, string Upstream, string Revision) Parse(string? version)
    {
        var text = version ?? string.Empty;
        var epoch = 0L;

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && long.TryParse(text.AsSpan(0, colon), out var parsed) && parsed >= 0)
        {
            epoch = parsed;
            text = text[(colon + 1)..];
        }

        var hyphen = text.LastIndexOf('-');
        return hyphen < 0
            ? (epoch, text, string.Empty)
            : (epoch, text[..hyphen], text[(hyphen + 1)..]);
    }

    /// <summary>dpkg's <c>verrevcmp</c>, applied to one fragment.</summary>
    private static int CompareFragment(string left, string right)
    {
        var i = 0;
        var j = 0;

        while (i < left.Length || j < right.Length)
        {
            var firstDifference = 0;

            // Non-digit run. The loop condition tests each side independently so that a string
            // which has run out is compared against whatever the other side still has, under an
            // ordering where the end of a string sorts after '~' and before every other
            // punctuation character.
            while ((i < left.Length && !IsDigit(left[i])) || (j < right.Length && !IsDigit(right[j])))
            {
                var a = Order(i < left.Length ? left[i] : '\0');
                var b = Order(j < right.Length ? right[j] : '\0');

                if (a != b)
                {
                    return a - b;
                }

                i++;
                j++;
            }

            // Leading zeros carry no value: 007 and 7 are the same number.
            while (i < left.Length && left[i] == '0')
            {
                i++;
            }

            while (j < right.Length && right[j] == '0')
            {
                j++;
            }

            // Digit run, compared by length first and by the first differing digit second — which
            // is numeric comparison without ever parsing a number, so an absurdly long run cannot
            // overflow anything.
            while (i < left.Length && IsDigit(left[i]) && j < right.Length && IsDigit(right[j]))
            {
                if (firstDifference == 0)
                {
                    firstDifference = left[i] - right[j];
                }

                i++;
                j++;
            }

            if (i < left.Length && IsDigit(left[i]))
            {
                return 1;
            }

            if (j < right.Length && IsDigit(right[j]))
            {
                return -1;
            }

            if (firstDifference != 0)
            {
                return firstDifference;
            }
        }

        return 0;
    }

    private static bool IsDigit(char value) => value is >= '0' and <= '9';

    /// <summary>dpkg's character ordering for the non-digit runs.</summary>
    /// <remarks>
    /// Four classes, and the two unusual ones are what make the rule worth transcribing rather
    /// than approximating. <c>~</c> sorts <i>before</i> the end of a string, which is what makes
    /// a release candidate older than its release. Everything that is neither a letter nor a
    /// digit nor <c>~</c> is pushed past the letters, which is what makes <c>1.0a</c> older than
    /// <c>1.0+b</c>.
    /// </remarks>
    private static int Order(char value) => value switch
    {
        '~' => -1,
        '\0' => 0,
        >= '0' and <= '9' => 0,
        >= 'a' and <= 'z' or >= 'A' and <= 'Z' => value,
        _ => value + 256,
    };
}
