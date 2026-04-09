using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class NormalizeVersionTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("V0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2.0.0", "0.2.0.0")]
    [InlineData("0.2.0+abc123", "0.2.0")]
    [InlineData("0.2.0-beta.1", "0.2.0")]
    [InlineData("v0.2.0-beta.1+abc123", "0.2.0")]
    [InlineData("  v0.2.0  ", "0.2.0")]
    public void NormalizeVersion_ParsesCommonFormats(string input, string expected)
    {
        var actual = UpdateCheckerService.NormalizeVersion(input);
        Assert.Equal(new System.Version(expected), actual);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("vvv")]
    [InlineData("+abc")]
    [InlineData("-beta")]
    public void NormalizeVersion_UnparseableInput_ReturnsZero(string input)
    {
        var actual = UpdateCheckerService.NormalizeVersion(input);
        Assert.Equal(new System.Version(0, 0, 0, 0), actual);
    }

    [Fact]
    public void NormalizeVersion_Null_ReturnsZero()
    {
        var actual = UpdateCheckerService.NormalizeVersion(null!);
        Assert.Equal(new System.Version(0, 0, 0, 0), actual);
    }
}
