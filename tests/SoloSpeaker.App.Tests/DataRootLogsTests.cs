using SoloSpeaker.App.Hosting;

namespace SoloSpeaker.App.Tests;

public sealed class DataRootLogsTests
{
    [Fact]
    public void Logs_for_is_under_the_given_root_rather_than_local_application_data()
    {
        string root = Path.GetFullPath(@"C:\solo-speaker-tests\instance-a");

        string logs = DataRoot.LogsFor(root);

        Assert.Equal(Path.Combine(root, "logs"), logs);
        Assert.False(
            logs.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Two_different_roots_have_two_different_log_directories()
    {
        string first = DataRoot.LogsFor(Path.GetFullPath(@"C:\solo-speaker-tests\instance-a"));
        string second = DataRoot.LogsFor(Path.GetFullPath(@"C:\solo-speaker-tests\instance-b"));

        Assert.NotEqual(first, second);
    }
}
