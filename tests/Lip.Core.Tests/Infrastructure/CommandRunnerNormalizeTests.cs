using Lip.Core.Infrastructure;

namespace Lip.Core.Tests.Infrastructure;

public class CommandRunnerNormalizeTests {
  [Fact]
  public void NormalizeScriptOutput_PlainText_IsUnchanged() {
    Assert.Equal(
        "one\ntwo\n",
        CommandRunner.NormalizeScriptOutput("one\r\ntwo\n"));
  }

  [Fact]
  public void NormalizeScriptOutput_RemovesEscapeSequences() {
    Assert.Equal(
        "RED-TEXT plain after",
        CommandRunner.NormalizeScriptOutput("\x1b[31mRED-TEXT\x1b[0m plain after"));
  }

  [Fact]
  public void NormalizeScriptOutput_CollapsesCarriageReturnFramesToTheLastOne() {
    string output = "2 / 3723 [>] 0.05%\r201 / 3723 [-->] 5.40%\r3723 / 3723 [-] 100.00%\n";

    Assert.Equal(
        "3723 / 3723 [-] 100.00%",
        CommandRunner.NormalizeScriptOutput(output).TrimEnd('\n'));
  }

  [Fact]
  public void NormalizeScriptOutput_KeepsCarriageReturnFreeLinesAroundRedrawnOnes() {
    string output = "before\rframe1\rframe2\nafter\n";

    Assert.Equal("frame2\nafter", CommandRunner.NormalizeScriptOutput(output).TrimEnd('\n'));
  }
}
