using System.Globalization;
using System.Text;

namespace FrameLink.Agent.Stage;

/// <summary>
/// The escape sequences the console stage uses, and nothing else.
/// </summary>
/// <remarks>
/// 256-colour sequences rather than truecolour: the Linux virtual console maps them down to its
/// own palette, so a frame renders in colour on <c>/dev/tty1</c> with no terminfo, no library and
/// no assumptions about the emulator — which is what §2.7 means by working "from the first second
/// of the first boot".
/// </remarks>
public static class Ansi
{
    /// <summary>Clears all attributes.</summary>
    public const string Reset = "\e[0m";

    /// <summary>Bold.</summary>
    public const string Bold = "\e[1m";

    /// <summary>Faint.</summary>
    public const string Dim = "\e[2m";

    /// <summary>Hides the cursor, so a repaint does not leave one blinking mid-frame.</summary>
    public const string HideCursor = "\e[?25l";

    /// <summary>Shows the cursor again.</summary>
    public const string ShowCursor = "\e[?25h";

    /// <summary>Moves the cursor to the top-left corner.</summary>
    public const string Home = "\e[H";

    /// <summary>Erases from the cursor to the end of the screen.</summary>
    public const string ClearToEnd = "\e[0J";

    /// <summary>Sets the foreground to a 256-colour index.</summary>
    public static string Foreground(int colour) =>
        string.Create(CultureInfo.InvariantCulture, $"\e[38;5;{colour}m");
}

/// <summary>Measuring and stripping text that carries escape sequences.</summary>
/// <remarks>
/// The renderer pads every line to the exact terminal width, so it has to know how wide a styled
/// string actually looks. Getting this wrong does not produce a subtle defect — it produces a box
/// whose right-hand border wanders, on the frame's own screen.
/// </remarks>
public static class AnsiText
{
    /// <summary>Removes every escape sequence.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!text.Contains('\e', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\e')
            {
                builder.Append(text[index]);
                continue;
            }

            index++;
            if (index < text.Length && text[index] == '[')
            {
                while (index < text.Length && !char.IsLetter(text[index]))
                {
                    index++;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>How many columns <paramref name="text"/> occupies once rendered.</summary>
    public static int VisibleLength(string text) => Strip(text).Length;
}
