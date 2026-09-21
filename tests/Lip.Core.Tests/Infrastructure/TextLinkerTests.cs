using Lip.Core.Infrastructure;

namespace Lip.Core.Tests.Infrastructure;

public class TextLinkerTests {
  [Fact]
  public void Linkify_PlainTextWithoutUrl_EscapesMarkupOnly() {
    Assert.Equal(
        "nothing special",
        TextLinker.Linkify("nothing special"));
  }

  [Fact]
  public void Linkify_ScriptOutputWithBrackets_IsEscaped() {
    // Script progress bars contain bracket sequences that must not be parsed as markup.
    Assert.Equal(
        @"2 / 3723 [[>____________________________________________________________________________]] 0.05%",
        TextLinker.Linkify("2 / 3723 [>____________________________________________________________________________] 0.05%"));
  }

  [Fact]
  public void Linkify_WrapsUrlsInLinks() {
    string result = TextLinker.Linkify("Downloaded from https://example.com/a.zip today");

    Assert.Contains("[link=https://example.com/a.zip]https://example.com/a.zip[/]", result);
    Assert.StartsWith("Downloaded from ", result);
    Assert.EndsWith(" today", result);
  }

  [Fact]
  public void Linkify_EscapesMarkupInsideUrls() {
    // A URL is unlikely to contain brackets, but the escaped form must never break the tag.
    string result = TextLinker.Linkify("see https://example.com/a[1].zip");

    Assert.DoesNotContain("[link=https://example.com/a[1].zip]", result);
    Assert.Contains("a[[1]].zip", result);
  }

  [Fact]
  public void Fit_TextWithinWidth_IsUnchanged() {
    Assert.Equal("short.zip", TextLinker.Fit("short.zip", 20));
  }

  [Fact]
  public void Fit_TextAtExactlyTheWidth_IsUnchanged() {
    Assert.Equal("exactly-ten", TextLinker.Fit("exactly-ten", 11));
  }

  [Fact]
  public void Fit_LongText_KeepsBothEndsAndFits() {
    string url = "https://gh-proxy.com/https://github.com/LiteLDev/LeviLamina/releases/download/x.zip";
    string fitted = TextLinker.Fit(url, 40);

    Assert.Equal(40, fitted.Length);
    Assert.StartsWith("https://gh-proxy", fitted);
    Assert.EndsWith("x.zip", fitted);
    Assert.Contains("…", fitted);
  }

  [Fact]
  public void Fit_ZeroWidth_ReturnsTheTextUnchanged() {
    // A profile without a width must not produce a negative-length string.
    Assert.Equal("anything", TextLinker.Fit("anything", 0));
  }
}
