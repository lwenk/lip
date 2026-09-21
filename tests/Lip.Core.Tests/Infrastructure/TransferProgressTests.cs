using Lip.Core.Infrastructure;

namespace Lip.Core.Tests.Infrastructure;

public class TransferProgressTests {
  [Theory]
  [InlineData(0, "0 B")]
  [InlineData(512, "512 B")]
  [InlineData(1024, "1.0 KiB")]
  [InlineData(1_572_864, "1.5 MB")]
  [InlineData(1_073_741_824, "1.0 GB")]
  public void FormatBytes_FormatsHumanReadableSizes(long bytes, string expected) {
    Assert.Equal(expected, TransferProgress.FormatBytes(bytes));
  }

  [Fact]
  public void Percentage_UnknownTotal_IsZero() {
    Assert.Equal(0, new TransferProgress(512, 0).Percentage);
  }
}
