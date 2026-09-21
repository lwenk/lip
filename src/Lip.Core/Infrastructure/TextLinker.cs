using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Lip.Core.Infrastructure;

/// <summary>
/// Prepares plain messages for Spectre markup rendering: everything is escaped so that script
/// output cannot be mistaken for markup, and URLs become clickable terminal hyperlinks. Because a
/// hyperlink is attached to every wrapped segment of the URL, clicking a long wrapped link still
/// opens the correct address.
/// </summary>
public static partial class TextLinker {
  [GeneratedRegex(@"https?://\S+")]
  private static partial Regex UrlRegex();

  public static string Linkify(string message) {
    StringBuilder builder = new();
    int lastIndex = 0;

    foreach (Match match in UrlRegex().Matches(message)) {
      builder.Append(Markup.Escape(message[lastIndex..match.Index]));
      builder.Append($"[link={Markup.Escape(match.Value)}]{Markup.Escape(match.Value)}[/]");
      lastIndex = match.Index + match.Length;
    }

    builder.Append(Markup.Escape(message[lastIndex..]));

    return builder.ToString();
  }

  /// <summary>
  /// Shortens text to the given width, keeping both ends. A URL keeps its host at the front and its
  /// file name at the back, so either end identifies it.
  /// </summary>
  public static string Fit(string text, int width) {
    if (text.Length <= width || width <= 0) {
      return text;
    }

    int available = Math.Max(width - 1, 0);
    int head = available / 2;

    return text[..head] + "…" + text[^(available - head)..];
  }
}
