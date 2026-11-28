using ShareFileWatch;
using Xunit;

public sealed class WindowActivationTests
{
    [Fact]
    public void FromArguments_CollectorRequiresTheExactSingleSwitch()
    {
        Assert.Equal(WindowRequest.Collector, WindowActivation.FromArguments(["--show-collector"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--show-monitor")]
    [InlineData("--SHOW-COLLECTOR")]
    [InlineData("--show-collector=true")]
    [InlineData(" --show-collector")]
    [InlineData("--unknown")]
    public void FromArguments_SendsOrdinaryRequestsToTheMonitor(string argument)
    {
        Assert.Equal(WindowRequest.Monitor, WindowActivation.FromArguments([]));
        Assert.Equal(WindowRequest.Monitor, WindowActivation.FromArguments([argument]));
        Assert.Equal(WindowRequest.Monitor, WindowActivation.FromArguments(["--show-collector", argument]));
    }

    [Fact]
    public void FromArguments_MultipleWindowFlagsDoNotActivateTheCollector()
    {
        Assert.Equal(WindowRequest.Monitor, WindowActivation.FromArguments(["--show-monitor", "--show-collector"]));
        Assert.Equal(WindowRequest.Monitor, WindowActivation.FromArguments(["--show-collector", "--show-collector"]));
    }
}
